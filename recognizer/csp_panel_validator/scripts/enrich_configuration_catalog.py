"""Annotate public definitions; never promote saved variants to live states."""
import json
from pathlib import Path
import shutil
import sqlite3
import sys

ROOT=Path(__file__).resolve().parents[3]
sys.path.insert(0,str(ROOT/'recognizer'))
from recognizer_core.panel_state.configuration_audit import FIELD_GROUPS,DESCRIPTIONS


def enrich(definitions):
    existing={d['key'] for d in definitions}
    for prefix,parent in [('tip','color_jitter.change_brush_tip_color'),('stroke','color_jitter.randomize_per_stroke')]:
        for channel,label,aliases in [
            ('hue','色相',['Hue','色相']),('saturation','饱和度',['Saturation','饱和度','飽和度']),
            ('brightness','明度',['Brightness','Luminosity','明度']),
            ('sub_color','与副色混合',['Blend with sub color','与副色混合','與副色混合'])]:
            key=f'color_jitter.{prefix}_{channel}'
            if key in existing:
                continue
            definitions.append(dict(id=key,key=key,category='Color Jitter',category_zh='顏色變化',
                label_en=aliases[0],label_zh_tw=label,aliases=aliases,value_kind='number',
                read_support='visible_number_checkbox_or_text_enum',internal_csp_id=None,range=None,
                enum_values=[],context_key=parent,scope='public_brush_settings',
                type_basis='public Color Jitter numeric sliders; requires visible parent heading',
                description_zh=f'{"每个前端" if prefix=="tip" else "每条笔触"}的{label}变化数值。',
                sources=[dict(url='https://help.clip-studio.com/en-us/manual_en/810_subtools/C.htm',
                              section='Color Jitter',item=aliases[0])]))
    for d in definitions:
        key=d['key']
        if key in FIELD_GROUPS:
            d['configuration_fields']=dict(columns=FIELD_GROUPS[key].split(),
                source='EditImageTool.todb/Variant',association='empiricalColumnAssociation',
                live_state=False,private_enum_codes_verified=False)
        base=key.removeprefix('dual.')
        if base in DESCRIPTIONS:
            d['description_zh']=DESCRIPTIONS[base]
        d['state_descriptions']=dict(
            ok='已识别当前截图中的值；可编辑位置需要另外确认。',
            partial='已匹配属性标签，当前值仍未完整确认。',
            disabled='当前截图灰显；不是数值零，也不是配置中没有该属性。',
            numeric='仅 number 类型且有独立数字位置的值可以由回放器修改。',
            highlight_index='当前面板选项顺序，从 1 开始；不是 CSP 私有数据库枚举代码。',
            compound='可能同时包含开关、模式、数值或动态子项；单一值不代表全部子状态。')
        if key in ('antialiasing','2_brush_shape.anti_aliasing'):
            d['state_descriptions']['options']=['无','弱','中','强']
        if key=='antialiasing':
            d['state_descriptions']['saved_code_evidence']='本机較硬：AntiAlias=2，与录制截图中（第 3 项）一致；其余代码按四档顺序推断。'
        if key=='erase.vector_eraser':
            d['aliases']=sorted(set(d['aliases']+['矢量擦除','向量橡皮擦']))
            d['state_descriptions'].update(toggle=['checked','unchecked'],
                modes=['擦除碰触的部分','擦至交点','擦除整条线'],
                private_mode_code_mapping='未验证；保留原始代码，不据此伪造实时状态。')
            evidence=dict(kind='observed_ui_alias',evidence='Memoline recorded Tool Property crop 2026-10-06 17:01:44; 矢量擦除')
            if evidence not in d['sources']:
                d['sources'].append(evidence)
        options={
            'ink.color_mixing':['Blend','Running color','Smear'],
            'ink.mixing_mode':['Standard','Perceptual'],
            'ink.blending_quality':['High quality','Normal'],
            'ink.brightness_correction':['五级亮度修正'],
            'ink.intensity_of_blur':['Automatic','Fixed value'],
            'anti_aliasing.speed_and_quality':['Automatic','High quality','Low quality'],
            'brush_tip.tip_shape':['Circle','Material'],
            'brush_tip.direction':['Horizontal','Vertical'],
            'brush_tip.flip_horizontal':['Off','Flip','Random','Invert on reversed strokes'],
            'brush_tip.flip_vertical':['Off','Flip','Random','Invert on reversed strokes'],
            'stroke.interval':['Fixed','Wide','Normal','Narrow'],
            'stroke.repeat_method':['Repeat','Reverse','Do not repeat','Random','One time only','One random cycle'],
            'starting_and_ending.how_to_specify':['Specify length','By percentage','Fade'],
            'correction.stabilization_mode':['慢速增强修正','快速减弱修正'],
        }
        if base in options:
            d['state_descriptions']['documented_options']=options[base]
        conditions={
            'ink.blending_mode':'混色开启且为 Blend / Running color 时不可调整；矢量图层的笔刷不可调整。',
            'ink.amount_of_paint':'需开启混色。',
            'ink.density_of_paint':'混色开启且为 Blend / Running color 时可调整。',
            'ink.blending_quality':'混色为 Running color 时可调整。',
            'ink.brightness_correction':'混色方式为 Perceptual 时可调整。',
            'brush_tip.hardness':'仅圆形前端适用；五格指示器是累积填色，不是互斥单选。',
            'brush_tip.flip_horizontal':'素材前端适用。',
            'brush_tip.flip_vertical':'素材前端适用。',
            'stroke.ribbon':'散布开启或圆形前端时不可用。',
            'erase.vector_eraser':'矢量图层适用；灰显时保留 disabled，不能把配置开关当作实时可用状态。',
            'starting_and_ending.starting':'Fade 模式不使用起笔设置。',
        }
        if base in conditions:
            d['state_descriptions']['availability']=conditions[base]
        if key.startswith('watercolor_edge.'):
            d['state_descriptions']['availability']='矢量图层和表现颜色为单色的图层不可用。'
        if key=='2_brush_shape.anti_aliasing':
            d['enum_values']=[['无','無','None'],['弱','Weak'],['中','Middle','Medium'],['强','強','Strong']]
    return definitions


def enrich_layers():
    path=ROOT/'recognizer/recognizer_core/layer_state/data/layer_catalog.json'
    payload=json.loads(path.read_text(encoding='utf-8'))
    modes=payload['blend_modes']
    for mode in modes:
        if mode['canonical']=='线性减淡':
            mode.update(canonical='添加（发光）',aliases=['添加（发光）','添加（發光）','Add (Glow)','add glow'])
        if mode['canonical']=='发光减淡':
            mode['aliases']+=['颜色减淡（发光）','顏色減淡（發光）']
        if mode['canonical']=='通过':
            mode['aliases']+=['穿透']
        if mode['canonical']=='亮度':
            mode['aliases']+=['brightness']
    additions=[('减去',['減去','subtract']),('深色',['darker color']),
               ('浅色',['淺色','lighter color']),('除法',['divide'])]
    for canonical,aliases in additions:
        if not any(m['canonical']==canonical for m in modes):
            modes.append(dict(canonical=canonical,aliases=[canonical,*aliases]))
    for mode in modes:
        mode['aliases']=list(dict.fromkeys(mode['aliases']))
    manual='https://help.clip-studio.com/en-us/manual_en/180_layers/'
    props=[]
    def add(key,label,kind,description,coverage,states=None,fields=None,source='Layer_properties.htm'):
        props.append(dict(key=key,label_zh=label,value_kind=kind,description_zh=description,
            state_descriptions=states or {},clip_state_coverage=coverage,clip_fields=fields or [],source=manual+source))
    add('name','图层名称','text','当前图层的名称；同名图层需要结构或 ID 消歧。','read',['名称'],['LayerName'],'180_layers.htm')
    add('selection','选中图层','identity','当前编辑的图层。','read',['selected','not_selected'],['CanvasCurrentLayer','LayerSelect'],'180_layers.htm')
    add('opacity','不透明度','number','图层整体的不透明度。','read',dict(display='百分比',saved='现有 .clip 核心换算原始值 / 256 × 100'),['LayerOpacity'],'180_layers.htm')
    add('blend_mode','混合模式','enum','图层与下方图层颜色的合成方式。','read',[m['canonical'] for m in modes],['LayerComposite'],'Blending_modes.htm')
    add('visible','可见性','checkbox','图层自身显示状态；父文件夹隐藏会影响有效可见性。','read',['visible','hidden'],['LayerVisibility'],'180_layers.htm')
    add('locked','锁定','checkbox','限制图层编辑。','read',['locked','unlocked'],['LayerLock'],'180_layers.htm')
    add('clipping','剪贴','checkbox','将图层内容限制在下方图层的不透明区域。','read',['clipped','not_clipped'],['LayerClip'],'180_layers.htm')
    add('draft','草稿','checkbox','标记为草稿图层。','read',['draft','normal'],['DraftLayer'],'180_layers.htm')
    add('folder','文件夹结构','structure','父子关系、层级和折叠状态。','read',['expanded','collapsed'],['LayerFolder','LayerFirstChildIndex','LayerNextIndex'],'180_layers.htm')
    add('mask','图层蒙版','compound','控制图层内容的遮盖；原始蒙版字段没有解码为全部状态。','partial',dict(has_mask='已提供',masking_raw='保留原始值，显示／链接语义未完整验证'),['LayerMasking','LayerType'],'Layer_masks.htm')
    add('reference','参考图层','checkbox','作为填充或防溢出的参考。','not_read',dict(status='unsupported'),'','180_layers.htm')
    add('lock_transparent','锁定透明像素','checkbox','保持图层透明区域。','not_read',dict(status='unsupported'),'','180_layers.htm')
    add('palette_color','面板颜色','color','图层面板中的标记色。','not_read',dict(status='unsupported'))
    add('border.edge','边缘效果','compound','边框的厚度、颜色与抗锯齿。','not_read',dict(toggle=['on','off'],parameters=['thickness','color','antialiasing']))
    add('border.watercolor','水彩边缘','compound','边缘的范围、不透明度、深浅与模糊。','not_read',dict(toggle=['on','off'],parameters=['range','opacity','darkness','blur']))
    add('extract_lines','提取线稿','compound','EX 的线条提取效果。','not_read',dict(toggle=['on','off'],edition='EX'))
    add('tone','网点','compound','网点频率、浓度、形状、角度和位置。','not_read',dict(toggle=['on','off'],parameters=['frequency','density','reflect_opacity','posterization','dot_shape','angle','size','factor','offset_x','offset_y','area_color']))
    add('layer_color','图层颜色','compound','用主色和副色替换黑白色。','not_read',dict(toggle=['on','off'],parameters=['layer_color','sub_color']))
    add('expression_color','表现颜色','enum','图层使用彩色、灰色或单色表现。','not_read',['Color','Gray','Monochrome'])
    add('expression_threshold','表现颜色阈值','compound','转换为单色时的颜色和透明度阈值。','not_read',dict(parameters=['color_threshold','alpha_threshold','reflect_opacity']))
    add('display_decrease_color','减少颜色显示','compound','素材显示使用有限颜色。','not_read',dict(toggle=['on','off'],parameters=['color_threshold','alpha_threshold','reflect_opacity']))
    add('mask_expression','蒙版表现','compound','允许渐变蒙版或使用阈值。','not_read',dict(show_gradients=['Yes','No'],parameters=['threshold']))
    add('tool_navigation','工具导航','action','显示可编辑当前图层的工具。','not_read',dict(state='导航控件，不是绘制属性'))
    add('overlay_texture','叠加纹理','compound','图像素材层的纹理叠加强度。','not_read',dict(toggle=['on','off'],parameters=['strength']))
    add('light_table_color','拷贝台颜色模式','enum','拷贝台图层的显示颜色方式。','not_read',['Color','Half color','Monochrome'])
    payload['properties']=props
    payload['metadata'].update(catalog_version='2026-10-06',
        public_sources=[manual+'Layer_properties.htm',manual+'Blending_modes.htm'],
        property_scope='Vocabulary and support audit. Descriptions do not imply that missing values are captured.',
        live_core_scope='currentLayerState supplies selected name; detailed read/partial fields above come from saved clipState.')
    path.write_text(json.dumps(payload,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')


def main():
    data=ROOT/'recognizer/csp_panel_validator/csp_panel_validator/data'
    source=data/'properties.sqlite3'
    with sqlite3.connect(source) as c:
        definitions=enrich([json.loads(r[0]) for r in c.execute('SELECT definition_json FROM properties ORDER BY id')])
        c.executemany('INSERT OR REPLACE INTO properties (id,definition_json) VALUES (?,?)',
            [(d['id'],json.dumps(d,ensure_ascii=False)) for d in definitions])
        metadata={r[0]:json.loads(r[1]) for r in c.execute('SELECT key,value_json FROM metadata')}
        metadata['catalog_version']='2026-10-06'
        metadata['configuration_comparison']=dict(source='Local CSP 5.0.0.0/EditImageTool.todb',
            scope='All non-null saved variant fields; private IDs and binary dynamics are explicitly unverified.',
            association_count=len(FIELD_GROUPS),description_count=sum('description_zh' in d for d in definitions))
        c.executemany('INSERT OR REPLACE INTO metadata VALUES (?,?)',[(k,json.dumps(v,ensure_ascii=False)) for k,v in metadata.items()])
    (data/'properties.json').write_text(json.dumps(dict(metadata=metadata,properties=definitions),ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    destination=ROOT/'recognizer/recognizer_core/panel_state/data/properties.sqlite3'
    shutil.copyfile(source,destination)
    enrich_layers()
    print(f'Annotated {len(definitions)} definitions; {sum("description_zh" in d for d in definitions)} concise descriptions.')


if __name__=='__main__':
    main()
