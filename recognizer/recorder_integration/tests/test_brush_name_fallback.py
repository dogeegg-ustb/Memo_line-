"""Fresh selected child OCR can resolve a corrupt Tool Property title."""
import queue
import threading
import uuid
from contextlib import contextmanager
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

import numpy as np
import cv2

sys.path.insert(0,str(Path(__file__).resolve().parents[2]))
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
sys.path.insert(0,str(Path(__file__).resolve().parents[3]/'CSP_Shortcut_Manager'))
from recognizer_core.panel_state import PanelStateCore
from recognizer_core.panel_state.models import OcrText
from recognizer_core.subtool_state import SubtoolPanelCore
from pipeline import Pipeline
from catalog import BRUSH,TOOLGROUP
from state_timeline import semantic_state


def word(text,x,y,w=60,h=18):
    return OcrText(text,.99,[[x,y],[x+w,y],[x+w,y+h],[x,y+h]])


@contextmanager
def image_directory(root):
    # Python 3.14's secure temp-directory ACL is incompatible with the managed
    # Windows sandbox. Use an ordinary workspace directory with inherited ACL.
    directory=root/uuid.uuid4().hex
    directory.mkdir()
    assert directory.resolve().is_relative_to(root.resolve())
    try:
        yield directory
    finally:
        for path in directory.iterdir():
            assert path.resolve().is_relative_to(directory.resolve()) and path.is_file()
            path.unlink()
        directory.rmdir()


class BrushNameFallbackTests(unittest.TestCase):
    def setUp(self):
        self.catalog=dict(nodes=[
            dict(kind='tool',name='橡皮擦',id='t',toolId='t',groupId=None),
            dict(kind='group',name='橡皮擦',id='g',toolId='t',groupId='g'),
            dict(kind='subtool',name='較硬',id='s',toolId='t',groupId='g'),
            dict(kind='subtool',name='柔軟',id='s2',toolId='t',groupId='g')])
        self.props=np.full((260,360,3),50,dtype=np.uint8)
        self.list=np.full((120,350,3),50,dtype=np.uint8)
        self.list[4:35,4:240]=(180,130,80)
        self.list[45:80,240:348]=(180,130,80)
        self.properties=[word('三 工具属性',10,4,150),word('笔刷尺寸',10,45),word('27.2',220,45,50)]
        self.subtools=[word('橡皮擦',35,10,65),word('较硬',285,54,45)]

    def process(self,subtools=None,image=None,properties=None):
        calls=[]
        def ocr(pixels):
            calls.append(pixels)
            return self.properties if pixels.shape == self.props.shape else (self.subtools if subtools is None else subtools)
        core=PanelStateCore(ocr_engine=ocr)
        if properties is not None:
            self.properties=properties
        result=core.process(self.props,tool_catalog=self.catalog,subtool_image=self.list if image is None else image,
                            subtool_evidence=dict(captureId='same-batch',screenshotId='child-crop',capturedTicks=100))
        return result,calls

    def test_corrupt_menu_title_falls_back_to_child_with_both_group_and_child_highlighted(self):
        result,calls=self.process()
        self.assertEqual(result['brush']['name'],'較硬')
        self.assertEqual(result['brush']['source'],'tool_list')
        resolution=result['brush']['name_resolution']
        self.assertEqual(resolution['source'],'selected_subtool')
        self.assertEqual(resolution['nodeId'],'s')
        self.assertEqual(resolution['rejectedNames'],['三'])
        self.assertEqual(resolution['companionEvidence']['captureId'],'same-batch')
        self.assertEqual(semantic_state('brushState',result)['name'],'較硬')
        self.assertEqual(len(calls),2)

    def test_valid_property_name_remains_primary_without_an_extra_ocr_pass(self):
        result,calls=self.process(properties=[word('較硬 工具属性',10,4,180),*self.properties[1:]])
        self.assertEqual(result['brush']['name_resolution']['source'],'tool_properties')
        self.assertEqual(len(calls),1)

    def test_highlighted_group_without_child_is_not_a_brush(self):
        result,_=self.process(subtools=self.subtools[:1])
        self.assertEqual(result['brush']['status'],'unknown')
        self.assertIsNone(semantic_state('brushState',result))

    def test_multiple_highlighted_children_and_ambiguous_catalog_are_rejected(self):
        image=self.list.copy()
        image[85:115,230:348]=(180,130,80)
        result,_=self.process(subtools=[*self.subtools,word('柔軟',280,90,45)],image=image)
        self.assertEqual(result['brush']['status'],'unknown')
        self.catalog['nodes'].append(dict(kind='subtool',name='較硬',id='duplicate',toolId='t',groupId='g'))
        result,_=self.process()
        self.assertEqual(result['brush']['status'],'unknown')

    def test_selected_group_disambiguates_duplicate_names_in_other_groups(self):
        self.catalog['nodes'].append(dict(kind='subtool',name='較硬',id='other',toolId='other-tool',groupId='other-group'))
        result,_=self.process()
        self.assertEqual(result['brush']['name_resolution']['nodeId'],'s')

    def test_missing_companion_and_saved_selection_do_not_confirm_an_unknown_name(self):
        self.catalog['savedCurrentNodeIds']=['s']
        result=PanelStateCore(ocr_engine=lambda _:self.properties).process(self.props,tool_catalog=self.catalog)
        self.assertEqual(result['brush']['status'],'unknown')
        self.assertIn('没有可见子工具',result['brush']['name_resolution']['reason'])

    def test_a_heading_sharing_a_child_name_is_not_promoted_to_a_child_row(self):
        self.catalog['nodes'].append(dict(kind='subtool',name='橡皮擦',id='same-name',toolId='t',groupId='g'))
        result=SubtoolPanelCore(ocr_engine=lambda _:self.subtools).process(self.list,catalog=self.catalog)
        self.assertEqual([e['name'] for e in result['entries']],['較硬'])
        self.assertEqual([e['name'] for e in result['groupEntries']],['橡皮擦'])
        header_only,_=self.process(subtools=self.subtools[:1])
        self.assertEqual(header_only['brush']['status'],'unknown')

    def test_requested_brush_captures_its_companion_in_one_batch_only_when_configured(self):
        pipe=Pipeline.__new__(Pipeline)
        pipe.stop=threading.Event()
        pipe.capture_queue=queue.Queue()
        pipe.capture({BRUSH},{BRUSH:[0,0,100,100],TOOLGROUP:[100,0,100,100]},100)
        request=pipe.capture_queue.get_nowait()
        self.assertEqual(request[0],{BRUSH,TOOLGROUP})
        pipe.capture({BRUSH},{BRUSH:[0,0,100,100]},110)
        self.assertEqual(pipe.capture_queue.get_nowait()[0],{BRUSH})

    def test_pipeline_uses_the_companion_from_this_job_and_exports_its_capture_identity(self):
        temporary_root=Path(__file__).resolve().parents[1]/'.test-temp'
        temporary_root.mkdir(exist_ok=True)
        with image_directory(temporary_root) as directory:
            self.assertTrue(Path(directory).resolve().is_relative_to(temporary_root.resolve()))
            paths={}
            for panel,image in [(BRUSH,self.props),(TOOLGROUP,self.list)]:
                path=Path(directory)/f'{len(paths)}.png'
                ok,png=cv2.imencode('.png',image)
                self.assertTrue(ok)
                png.tofile(path)
                paths[panel]=dict(path=str(path),screenshotId=f'image-{len(paths)}',
                    roi=[10,20,image.shape[1],image.shape[0]],capturedTicks=100,captureEndTicks=105)
            emitted=[]
            pipe=Pipeline.__new__(Pipeline)
            pipe.emit=emitted.append
            pipe.analysis_lock=threading.RLock()
            pipe.panel_activity={}
            pipe.settings=dict(analysisQuietMs=150)
            pipe.tool_catalog=self.catalog
            pipe.timeline=__import__('state_timeline').StateTimeline(lambda _:None,lambda _:None,pipe._publish_core_update)
            pipe.timeline.expect('p',{'brushState'})
            pipe.cores={BRUSH:PanelStateCore(ocr_engine=lambda image:self.properties if image.shape==self.props.shape else self.subtools)}
            job=dict(module=BRUSH,crops=paths,ticks=105,refs=[],captureId='fresh-job',packageId='p',
                triggerTicks=90,captureEndTicks=105,captureLatencyMs=15,captureDurationMs=5,
                captureBudgetExceeded=False,reason='explicitRequest',initialize=False,causalAmbiguous=True,observedAfterEventId=None)
            with patch('pipeline.now_ticks',return_value=200):
                pipe._analyze(job)
            data=emitted[-1]['data']
            self.assertEqual(data['status'],'changed',data.get('error'))
            self.assertEqual(data['state']['name'],'較硬')
            evidence=data['rawResult']['brush']['name_resolution']['companionEvidence']
            self.assertEqual(evidence['captureId'],'fresh-job')
            self.assertEqual(evidence['screenshotId'],'image-1')
            self.assertEqual(evidence['capturedTicks'],100)


if __name__=='__main__':
    unittest.main()
