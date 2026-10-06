from pathlib import Path
import sys
import unittest
from unittest.mock import patch
import threading

import numpy as np

ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT))
from recognizer_core.subtool_state import SubtoolPanelCore
from recognizer_core.panel_state.models import OcrText
from recognizer_core.panel_state.ocr_engine import OcrUnavailable
import test_shortcut_api as fixtures
from pipeline import Pipeline
from state_timeline import StateTimeline
from catalog import TOOLGROUP
import cv2


def word(text,x,y,w=60,h=18):
    return OcrText(text,.99,[[x,y],[x+w,y],[x+w,y+h],[x,y+h]])


class SubtoolOcrTests(unittest.TestCase):
    def setUp(self):
        self.fixture=fixtures.ShortcutApiTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.catalog=fixtures.read_configuration(self.fixture.root)['toolCatalog']

    def process(self,items,image=None,catalog=None):
        image=np.full((220,300,3),50,dtype=np.uint8) if image is None else image
        return SubtoolPanelCore(ocr_engine=lambda _:items).process(image,catalog=catalog or self.catalog)

    def test_returns_name_boxes_and_all_duplicate_identities_but_excludes_panel_chrome(self):
        items=[word('子工具[沾水笔]',10,5,160),word('G笔',200,40,30),word('圆笔',200,80,40),
               word('添加下载的素材',80,150,160)]
        result=self.process(items)
        self.assertEqual(result['status'],'ok')
        self.assertEqual([entry['name'] for entry in result['entries']],['G筆','圓筆'])
        self.assertEqual([entry['bbox'] for entry in result['entries']],[[200,40,30,18],[200,80,40,18]])
        self.assertEqual(result['entries'][0]['matchStatus'],'ambiguous')
        self.assertEqual([match['id'] for match in result['entries'][0]['matches']],['tool_12','tool_22'])
        self.assertEqual(result['entries'][1]['matches'][0]['path'],['毛笔','沾水笔','圓筆'])
        self.assertEqual(len(result['unmatchedText']),2)
        self.assertTrue(all('bbox' not in item for item in result['unmatchedText']))

    def test_combines_split_brush_name_segments_into_its_own_union_box(self):
        result=self.process([word('签字',160,40,40),word('笔',205,40,20)])
        self.assertEqual(result['entries'][0]['name'],'签字笔')
        self.assertEqual(result['entries'][0]['bbox'],[160,40,65,18])

    def test_visible_group_names_have_separate_locations_and_matching_node_paths(self):
        result=self.process([word('子工具[毛笔]',10,5,180),word('沾水笔',10,30,60),word('麦克笔',100,30,60),
                             word('圆笔',200,80,40)])
        self.assertEqual([entry['name'] for entry in result['groupEntries']],['沾水笔','麦克笔'])
        self.assertEqual(result['groupEntries'][1]['matches'][0]['id'],'tool_14')
        self.assertEqual(result['groupEntries'][0]['kind'],'group')
        self.assertEqual(len(result['entries']),1)

    def test_clips_out_of_bounds_names_and_does_not_use_saved_selection_as_live_evidence(self):
        result=self.process([word('圆笔',280,40,40)])
        self.assertEqual(result['entries'][0]['bbox'],[280,40,20,18])
        self.assertEqual(result['entries'][0]['selectionState'],'unknown')
        self.assertEqual(result['selectionSource'],'imageBackgroundHeuristic')

    def test_selected_background_is_current_image_evidence(self):
        image=np.full((220,300,3),50,dtype=np.uint8)
        image[35:65,185:255]=(180,130,80)
        result=self.process([word('圆笔',200,40,40)],image)
        self.assertEqual(result['entries'][0]['selectionState'],'selected')

    def test_no_matched_names_and_missing_catalog_do_not_invent_positions(self):
        self.assertEqual(self.process([])['status'],'unknown')
        result=SubtoolPanelCore(ocr_engine=lambda _:[word('G笔',200,40,30)]).process(
            np.zeros((100,300,3),dtype=np.uint8),catalog={})
        self.assertEqual(result['status'],'unavailable')
        self.assertEqual(result['entries'],[])

    def test_hidden_names_are_not_exported_as_visible_subtools(self):
        for node in self.catalog['nodes']:
            if node['name']=='G筆':
                node['hidden']=True
        result=self.process([word('G笔',200,40,30)])
        self.assertEqual(result['entries'],[])

    def test_ocr_failure_is_propagated_to_the_pipeline_error_path(self):
        def fail(_):
            raise OcrUnavailable('fixture error')
        with self.assertRaises(OcrUnavailable):
            SubtoolPanelCore(ocr_engine=fail).process(np.zeros((100,300,3),dtype=np.uint8),catalog=self.catalog)

    def test_live_projection_keeps_same_frame_screen_coordinates_and_refreshes_unchanged_locations(self):
        emitted=[]
        pipe=Pipeline.__new__(Pipeline)
        pipe.emit=emitted.append
        timeline=StateTimeline(lambda _:None,lambda _:None,pipe._publish_core_update)
        for capture,x in [('first',200),('second',210)]:
            result=self.process([word('圆笔',x,40,40)])
            with patch('pipeline.now_ticks',return_value=200):
                timeline.observe('subtoolState',result,dict(panelRoi=[-300,500,300,220],captureId=capture))
            data=emitted[-1]['data']
            self.assertEqual(data['ocrEntries'][0]['screenBbox'],[-300+x,540,40,18])
            self.assertEqual(data['evidence']['captureId'],capture)
        self.assertEqual(emitted[-1]['data']['status'],'unchanged')
        with patch('pipeline.now_ticks',return_value=300):
            timeline.observe('subtoolState',None,{},'OCR failed')
        self.assertEqual(emitted[-1]['data']['ocrEntries'],[])
        self.assertEqual(emitted[-1]['data']['status'],'error')

    def test_pipeline_analyzes_the_subtool_crop_and_keeps_frame_identity_on_failure(self):
        image_path=self.fixture.root/'panel.png'
        encoded,png=cv2.imencode('.png',np.full((220,300,3),50,dtype=np.uint8))
        self.assertTrue(encoded)
        png.tofile(image_path)
        emitted=[]
        pipe=Pipeline.__new__(Pipeline)
        pipe.emit=emitted.append
        pipe.analysis_lock=threading.RLock()
        pipe.panel_activity={}
        pipe.settings=dict(analysisQuietMs=150)
        pipe.tool_catalog=self.catalog
        pipe.timeline=StateTimeline(lambda _:None,lambda _:None,pipe._publish_core_update)
        job=dict(module=TOOLGROUP,crops={TOOLGROUP:dict(path=str(image_path),screenshotId='image1',roi=[-300,500,300,220])},
            ticks=100,refs=[7],captureId='capture1',packageId='p',triggerTicks=90,captureEndTicks=105,
            captureLatencyMs=15,captureDurationMs=5,captureBudgetExceeded=False,reason='operation',initialize=False,
            causalAmbiguous=False,observedAfterEventId=7)
        pipe.timeline.expect('p',{'subtoolState'})
        pipe.cores={TOOLGROUP:SubtoolPanelCore(ocr_engine=lambda _:[word('沾水笔',10,20,60),word('圆笔',200,80,40)])}
        with patch('pipeline.now_ticks',return_value=200):
            pipe._analyze(job)
        data=emitted[-1]['data']
        self.assertEqual(data['status'],'changed',data.get('error'))
        self.assertEqual(data['ocrEntries'][0]['screenBbox'],[-100,580,40,18])
        self.assertEqual(data['groupEntries'][0]['screenBbox'],[-290,520,60,18])
        self.assertEqual(data['evidence']['captureEndTicks'],105)
        self.assertEqual(data['evidence']['capturedTicks'],100)
        self.assertEqual(data['evidence']['captureId'],'capture1')
        def fail(_):
            raise OcrUnavailable('fixture failure')
        pipe.cores={TOOLGROUP:SubtoolPanelCore(ocr_engine=fail)}
        pipe.timeline.expect('p2',{'subtoolState'})
        job['packageId']='p2'
        job['captureId']='capture2'
        with patch('pipeline.now_ticks',return_value=300):
            pipe._analyze(job)
        data=emitted[-1]['data']
        self.assertEqual(data['status'],'error')
        self.assertEqual(data['ocrEntries'],[])
        self.assertEqual(data['groupEntries'],[])
        self.assertEqual(data['evidence']['panelRoi'],[-300,500,300,220])
        self.assertEqual(data['evidence']['captureId'],'capture2')

    def test_hidden_subtool_panel_does_not_add_an_initialization_dependency(self):
        for visible in (False,True):
            pipe=Pipeline.__new__(Pipeline)
            pipe.timeline=StateTimeline(lambda _:None,lambda _:None)
            pipe.configure([100,100],[0,0,10,10],None,'initial',subtools_available=visible)
            self.assertEqual('subtoolState' in pipe.timeline.packages['initial']['expected'],visible)


if __name__=='__main__':
    unittest.main()
