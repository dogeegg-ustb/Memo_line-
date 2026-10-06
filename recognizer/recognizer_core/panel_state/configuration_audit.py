"""Read-only comparison of saved CSP variants and the recognition catalog.

UI translation IDs, VariantShowParam integers and SQLite column names belong
to different namespaces. Column associations below are empirical annotations;
they are never used as live state or numeric click locations.
"""
from __future__ import annotations

import argparse
from collections import Counter
import csv
import hashlib
import json
from pathlib import Path
import sqlite3
import struct
import sys

# Public control -> saved configuration columns. Some controls have a toggle,
# value and dynamics blob; every raw column remains independently visible.
FIELD_GROUPS = {
    'brush_size': 'BrushSize BrushSizeUnit BrushSizeEffector',
    'brush_size.specify_by_size_on_screen': 'BrushSizeSyncViewScale',
    'brush_size.at_least_1_pixel': 'BrushAtLeast1Pixel',
    'opacity': 'Opacity BrushOpacityEffector',
    'ink.blending_mode': 'CompositeMode',
    'ink.color_mixing': 'BrushUseWaterColor BrushUseWaterColor2 BrushWaterColor',
    'ink.mixing_mode': 'BrushColorMixingMode',
    'ink.brightness_correction': 'BrushLMSLinearity',
    'ink.amount_of_paint': 'BrushMixColor BrushMixColorEffector',
    'ink.density_of_paint': 'BrushMixAlpha BrushMixAlphaEffector',
    'ink.color_stretch': 'BrushMixColorExtension',
    'ink.intensity_of_blur': 'BrushBlur BrushBlurUnit BrushBlurEffector BrushBlurLinkSize BrushBlurKind',
    'color_jitter.change_brush_tip_color': 'BrushChangePatternColor',
    'color_jitter.tip_hue': 'BrushHueChange BrushHueChangeEffector',
    'color_jitter.tip_saturation': 'BrushSaturationChange BrushSaturationChangeEffector',
    'color_jitter.tip_brightness': 'BrushValueChange BrushValueChangeEffector',
    'color_jitter.tip_sub_color': 'BrushSubColor BrushSubColorEffector',
    'color_jitter.randomize_per_stroke': 'BrushChangeStrokeColor',
    'color_jitter.stroke_hue': 'BrushStrokeHueChange',
    'color_jitter.stroke_saturation': 'BrushStrokeSaturationChange',
    'color_jitter.stroke_brightness': 'BrushStrokeValueChange',
    'color_jitter.stroke_sub_color': 'BrushStrokeSubColor',
    'color_jitter.change_target': 'BrushChangeColorTarget',
    'antialiasing': 'AntiAlias',
    'anti_aliasing.speed_and_quality': 'BrushQuality',
    'brush_tip.tip_shape': 'BrushUsePatternImage',
    'brush_tip.brush_tip_icon': 'BrushPatternImageArray BrushPatternNameVersion',
    'brush_tip.hardness': 'BrushHardness',
    'brush_tip.thickness': 'BrushThickness BrushThicknessEffector',
    'brush_tip.direction': 'BrushVerticalThicknes',
    'brush_tip.angle': 'BrushRotation BrushRotationEffector BrushRotationRandomScale',
    'brush_tip.flip_horizontal': 'BrushPatternReverseHorizontal',
    'brush_tip.flip_vertical': 'BrushPatternReverseVertical',
    'brush_tip.brush_density': 'BrushFlow BrushFlowEffector',
    'brush_tip.adjust_brush_density_by_gap': 'BrushAdjustFlowByInterval',
    'spraying_effect.spraying_effect': 'BrushUseSpray',
    'spraying_effect.particle_size': 'BrushSpraySize BrushSpraySizeUnit BrushSpraySizeEffector',
    'spraying_effect.brush_size': 'BrushSpraySizeSyncBrushSize',
    'spraying_effect.particle_density': 'BrushSprayDensity BrushSprayDensityEffector',
    'spraying_effect.spray_deviation': 'BrushSprayBias BrushSprayUseFixedPoint BrushSprayFixedPointArray',
    'spraying_effect.direction_of_particle': 'BrushRotationInSpray BrushRotationEffectorInSpray BrushRotationRandomInSpray',
    'stroke.interval': 'BrushInterval BrushIntervalEffector BrushAutoIntervalType',
    'stroke.continuous_spraying': 'BrushContinuousPlot',
    'stroke.correct_velocity_input': 'BrushAdjustVelocity',
    'stroke.ribbon': 'BrushRibbon',
    'stroke.repeat_method': 'BrushPatternOrderType BrushPatternOrderType2 BrushPatternReverse',
    'stroke.blend_brush_tips_with_darken': 'BrushBlendPatternByDarken',
    'texture.texture': 'TextureImage',
    'texture.texture_density': 'TextureDensity TextureDensityEffector',
    'texture.invert_texture': 'TextureReverseDensity',
    'texture.emphasize_density': 'TextureStressDensity',
    'texture.scale_ratio': 'TextureScale TextureScale2',
    'texture.rotation_angle': 'TextureRotate',
    'texture.brightness': 'TextureBrightness',
    'texture.contrast': 'TextureContrast',
    'texture.texture_mode': 'TextureCompositeMode',
    'texture.apply_by_each_plot': 'TextureForPlot',
    '2_brush_shape.dual_brush': 'UseDualBrush',
    '2_brush_shape.mode': 'DualBrushCompositeMode',
    '2_brush_shape.apply_rgb_value': 'ChangeRGBByDual',
    '2_brush_shape.brush_size': 'DualSize DualSizeUnit DualSizeEffector',
    '2_brush_shape.link_to_main_brush_size': 'SyncDualBrushSize',
    '2_brush_shape.anti_aliasing': 'DualAntiAlias',
    'watercolor_edge.watercolor_edge': 'BrushUseWaterEdge BrushWaterEdgeRadius BrushWaterEdgeRadiusUnit',
    'watercolor_edge.opacity': 'BrushWaterEdgeAlphaPower',
    'watercolor_edge.darkness': 'BrushWaterEdgeValuePower',
    'watercolor_edge.process_after_brush_stroke': 'BrushWaterEdgeAfterDrag',
    'watercolor_edge.blurring_width': 'BrushWaterEdgeBlur BrushWaterEdgeBlurUnit',
    'erase.vector_eraser': 'BrushUseVectorEraser BrushVectorEraserType',
    'erase.refer_all_layers': 'BrushVectorEraserReferAllLayer',
    'erase.erase_on_all_layers': 'BrushEraseAllLayer',
    'correction.sharp_angles': 'BrushSharpenCorner',
    'stabilization': 'FlickerReduction',
    'adjust_stabilization_by_speed': 'FlickerReductionBySpeed FlickerReductionBySpeedType',
    'correction.post_correction': 'BrushUseRevision BrushRevision',
    'correction.adjust_by_speed_post_correction': 'BrushRevisionBySpeed',
    'correction.adjust_by_scale': 'BrushRevisionByViewScale',
    'correction.bezier_curve': 'BrushRevisionBezier',
    'correction.taper': 'Stickness',
    'correction.enable_snapping': 'BrushEnableSnap EnableSnapSymmetry',
    'correction.vector_magnet': 'BrushUseVectorMagnet BrushVectorMagnetPower',
    'starting_and_ending.starting_and_ending': 'BrushInOutTarget',
    'starting_and_ending.how_to_specify': 'BrushInOutType',
    'starting_and_ending.starting': 'BrushUseIn BrushInLength BrushInLengthUnit BrushInRatio',
    'starting_and_ending.ending': 'BrushUseOut BrushOutLength BrushOutLengthUnit BrushOutRatio',
    'starting_and_ending.starting_and_ending_by_speed': 'BrushInOutBySpeed',
    'anti_overflow.do_not_cross_lines_of_reference_layer': 'BrushUseReferLayer',
    'anti_overflow.fill_up_to_vector_path': 'FillReferVectorCenter',
    'anti_overflow.color_margin': 'FillColorMargin',
    'anti_overflow.area_scaling': 'FillUseExpand FillExpandLength FillExpandLengthUnit',
    'anti_overflow.scaling_mode': 'FillExpandType',
}
for key, columns in list(FIELD_GROUPS.items()):
    if key.startswith(('brush_tip.', 'spraying_effect.', 'stroke.', 'texture.')):
        dual = columns.replace('Brush', 'Dual').replace('Texture', 'DualTexture')
        FIELD_GROUPS['dual.'+key] = dual
FIELD_GROUPS['dual.spraying_effect.brush_size'] = 'DualSpraySizeSyncBrushSize'
FIELD_GROUPS['dual.stroke.blend_brush_tips_with_darken'] += ' DualBlendPatternByDarken2'


DESCRIPTIONS = {
    'brush_size': '绘制线条的笔刷尺寸；动态设置可影响实际笔触尺寸。',
    'opacity': '笔触不透明度，与笔刷前端各印记的浓度不同。',
    'ink.color_mixing': '控制已有颜色与当前笔触的混合，包含开关、混合方式和质量。',
    'ink.amount_of_paint': '控制混色时加入的绘画颜色。',
    'ink.density_of_paint': '控制混色时绘画颜色的浓度。',
    'ink.color_stretch': '控制已有颜色被笔触拖延的程度。',
    'ink.intensity_of_blur': '控制模糊强度及其与笔刷尺寸的联动。',
    'color_jitter.change_brush_tip_color': '每个笔刷前端的色相、饱和度、明度与副色混合动态。',
    'color_jitter.randomize_per_stroke': '每条笔触的颜色随机变化。',
    'antialiasing': '平滑线条和边缘，四档依次为无、弱、中、强。',
    'anti_aliasing.speed_and_quality': '大尺寸笔刷绘制时选择速度与画质的优先级。',
    'brush_tip.hardness': '圆形笔刷前端的柔化程度；素材前端不适用。',
    'brush_tip.thickness': '沿设定方向改变笔刷前端的厚度。',
    'brush_tip.direction': '厚度调整所应用的水平或垂直方向。',
    'brush_tip.angle': '笔刷前端方向及其动态来源。',
    'brush_tip.brush_density': '每个笔刷前端印记的不透明度。',
    'brush_tip.adjust_brush_density_by_gap': '随印记间距调整浓度，使整体不透明度近似恒定。',
    'spraying_effect.spraying_effect': '分散绘制笔刷前端印记。',
    'spraying_effect.particle_size': '散布时每个笔刷前端的尺寸。',
    'spraying_effect.particle_density': '散布时笔刷前端印记的数量密度。',
    'stroke.interval': '连续笔刷前端印记之间的间隔。',
    'texture.texture': '笔触使用的纸质素材。',
    'texture.texture_density': '纸质效果的强度。',
    'texture.invert_texture': '反转纸质素材的明暗。',
    'texture.scale_ratio': '纸质素材的缩放比例。',
    'texture.rotation_angle': '纸质素材的旋转角度。',
    'texture.brightness': '纸质素材的明暗调整。',
    'texture.contrast': '纸质素材明暗对比调整。',
    '2_brush_shape.dual_brush': '将第二笔刷与主笔刷组合。',
    'watercolor_edge.watercolor_edge': '给笔触边缘添加水彩边界效果及宽度。',
    'watercolor_edge.opacity': '水彩边界的不透明度。',
    'watercolor_edge.darkness': '水彩边界的深浅。',
    'watercolor_edge.blurring_width': '水彩边界的模糊宽度。',
    'erase.vector_eraser': '在矢量图层启用矢量擦除；模式决定擦除的线段范围。',
    'erase.refer_all_layers': '擦至交点时也参考其他矢量图层。',
    'erase.erase_on_all_layers': '对所有可擦除图层执行擦除。',
    'stabilization': '修正输入轨迹的抖动。',
    'correction.post_correction': '落笔完成后修正轨迹。',
    'correction.vector_magnet': '将矢量笔触连接或吸附到已有矢量线。',
    'starting_and_ending.starting': '起笔端的变化长度或比例。',
    'starting_and_ending.ending': '收笔端的变化长度或比例。',
    'anti_overflow.do_not_cross_lines_of_reference_layer': '将笔触限制在参考图层线条的一侧。',
    'anti_overflow.color_margin': '识别参考线条时允许的颜色差异。',
    'anti_overflow.area_scaling': '扩张或收缩可绘制的区域。',
}
DESCRIPTIONS.update({
    'brush_size.specify_by_size_on_screen':'随画布显示倍率调整笔刷尺寸，使屏幕尺寸保持近似恒定。',
    'brush_size.at_least_1_pixel':'细线至少保留一个像素。',
    'brush_size.brush_preview':'显示笔刷尺寸和前端形状的预览。',
    'brush_size.disarray_for_focus_lines_speed_lines_only':'集中线／速度线的线宽变化比例。',
    'ink.blending_mode':'笔触与已有颜色的合成方式。',
    'ink.blending_quality':'混色的处理质量设置。',
    'ink.mixing_mode':'选择混合颜色的处理方式。',
    'ink.brightness_correction':'调整混色时的亮度处理。',
    'color_jitter.change_target':'选择发生颜色变化的颜色目标。',
    'brush_shape.add_to_presets':'将当前笔刷形状参数保存为预设。',
    'brush_shape.brush_shape_preview':'显示笔刷形状的笔触预览。',
    'brush_shape.brush_shape':'选择笔刷形状预设。',
    'brush_shape.apply_brush_shape':'用所选预设替换当前形状参数。',
    'brush_shape.change_name_of_selected_brush_shape':'更改所选形状预设名称。',
    'brush_shape.delete_selected_brush_shape':'删除所选形状预设。',
    'brush_tip.tip_shape':'选择圆形前端或素材前端。',
    'brush_tip.brush_tip_icon':'显示当前素材前端列表。',
    'brush_tip.add_brush_tip_shape':'添加素材前端。',
    'brush_tip.delete_selected_brush_tip_shape':'删除所选素材前端。',
    'brush_tip.flip_horizontal':'水平翻转素材前端的方式。',
    'brush_tip.flip_vertical':'垂直翻转素材前端的方式。',
    'spraying_effect.brush_size':'粒子尺寸跟随主笔刷尺寸。',
    'spraying_effect.spray_deviation':'调整散布粒子的位置偏向。',
    'spraying_effect.direction_of_particle':'调整散布粒子的方向。',
    'stroke.continuous_spraying':'笔尖保持按压时持续绘制。',
    'stroke.correct_velocity_input':'修正供笔刷动态使用的输入速度。',
    'stroke.ribbon':'使素材前端沿笔触连续变形。',
    'stroke.repeat_method':'多个素材前端的绘制顺序。',
    'stroke.blend_brush_tips_with_darken':'前端重叠时使用较暗颜色。',
    'texture.emphasize_density':'强调纸质浓度。',
    'texture.texture_mode':'纸质与笔触的合成方式。',
    'texture.apply_by_each_plot':'对每个前端印记分别应用纸质。',
    '2_brush_shape.mode':'第二笔刷与主笔刷的合成方式。',
    '2_brush_shape.apply_rgb_value':'二重笔刷合成是否应用 RGB 颜色。',
    '2_brush_shape.brush_size':'第二笔刷的尺寸。',
    '2_brush_shape.link_to_main_brush_size':'第二笔刷尺寸联动主笔刷尺寸。',
    '2_brush_shape.anti_aliasing':'第二笔刷的抗锯齿强度。',
    '2_brush_shape.brush_shape_preset':'第二笔刷的形状预设列表。',
    '2_brush_shape.apply_brush_shape':'应用第二笔刷形状预设。',
    '2_brush_shape.preset_of_brush_shape_settings':'保存第二笔刷形状预设。',
    '2_brush_shape.delete_brush_shape':'删除第二笔刷形状预设。',
    'watercolor_edge.process_after_brush_stroke':'完成笔触后处理水彩边界。',
    'correction.sharp_angles':'使笔触的转角保持尖锐。',
    'adjust_stabilization_by_speed':'手颤修正强度随绘制速度改变。',
    'correction.stabilization_mode':'慢速增强或快速减弱手颤修正。',
    'correction.adjust_by_speed_post_correction':'后修正强度随输入速度改变。',
    'correction.adjust_by_scale':'后修正强度随显示倍率改变。',
    'correction.bezier_curve':'后修正使用贝塞尔曲线。',
    'correction.taper':'延长逐渐减压时笔触变细的部分。',
    'correction.enable_snapping':'启用视图菜单所配置的吸附。',
    'correction.snap_to_default_border':'吸附到基本边框。',
    'starting_and_ending.starting_and_ending':'选择起笔收笔所影响的参数和最小值。',
    'starting_and_ending.how_to_specify':'按长度、百分比或渐隐设定变化范围。',
    'starting_and_ending.starting_and_ending_by_speed':'输入速度影响起笔收笔效果。',
    'anti_overflow.fill_up_to_vector_path':'参考矢量线中心路径限制填绘区域。',
    'anti_overflow.scaling_mode':'扩张／收缩区域时的角部处理。',
    'angle_dynamics.none':'前端方向不随笔操作变化。',
    'angle_dynamics.direction_of_pen':'笔的朝向影响前端方向。',
    'angle_dynamics.pen_tilt':'笔杆旋转影响前端方向。',
    'angle_dynamics.direction_of_line':'沿笔触行进方向改变前端方向。',
    'angle_dynamics.random':'随机改变前端方向。',
    'dynamics_settings_direction_of_particle.none':'散布粒子方向不随笔操作变化。',
    'dynamics_settings_direction_of_particle.direction_of_line':'粒子方向跟随行进方向。',
    'dynamics_settings_direction_of_particle.direction_of_whole_spray':'粒子方向跟随整体散布方向。',
    'dynamics_settings_direction_of_particle.spray_toward_center':'粒子朝散布中心定向。',
    'dynamics_settings_direction_of_particle.random':'随机改变粒子方向。',
    'color_tolerance':'判定相近颜色为同一区域的容差。',
    'close_gap':'忽略线条小缺口以限制填充区域。',
    'area_scaling':'扩张或收缩填充／选择区域。',
    'creation_mode':'新选区与已有选区的组合方式。',
    'contiguous_pixels':'将范围限制为颜色连续的相邻像素。',
    'reference_layers':'指定填充或选择所参考的图层范围。',
})


def raw_value(value):
    if isinstance(value, bytes):
        return dict(kind='binary', byteLength=len(value), sha256=hashlib.sha256(value).hexdigest())
    return value


def audit(tool_db, property_db, inventory):
    with sqlite3.connect(property_db.resolve().as_uri()+'?mode=ro',uri=True) as c:
        definitions = [json.loads(r[0]) for r in c.execute('SELECT definition_json FROM properties')]
    by_key = {d['key']:d for d in definitions}
    field_to_key = {field:key for key, fields in FIELD_GROUPS.items() for field in fields.split()}
    brushes, missing, columns_used = [], Counter(), Counter()
    with sqlite3.connect(tool_db.resolve().as_uri()+'?mode=ro',uri=True) as c:
        c.row_factory = sqlite3.Row
        columns = [r[1] for r in c.execute('PRAGMA table_info(Variant)')]
        nodes = {f"tool_{r['_PW_ID']}":dict(r) for r in c.execute('SELECT * FROM Node')}
        variants = {r['VariantID']:dict(r) for r in c.execute('SELECT * FROM Variant')}
    for node in inventory['nodes']:
        if node['kind'] != 'subtool':
            continue
        source = nodes[node['id']]
        variant = variants.get(source['NodeVariantID'])
        if variant is None:
            brushes.append(dict(id=node['id'], name=node['name'], path=node['path'], status='missingSavedVariant'))
            continue
        params, unmapped = [], []
        for column,value in variant.items():
            if value is None or column in ('_PW_ID','VariantID','VariantShowSeparator','VariantShowParam'):
                continue
            columns_used[column] += 1
            key = field_to_key.get(column)
            definition = by_key.get(key)
            entry = dict(field=column, value=raw_value(value), propertyKey=key,
                         association='empiricalColumnAssociation' if key else 'unmapped',
                         catalogStatus='present' if definition else 'missingDefinition' if key else 'unmapped')
            if definition:
                entry['valueCoverage'] = 'binaryDynamicsNotDecoded' if isinstance(value,bytes) else (
                    'compoundAssociation' if definition['value_kind']=='compound' else 'controlAssociation')
            params.append(entry)
            if not definition:
                unmapped.append(column)
                missing[column] += 1
        show = variant.get('VariantShowParam') or b''
        show_ids = list(struct.unpack('>'+str(len(show)//4)+'I',show)) if len(show)%4 == 0 else None
        brushes.append(dict(id=node['id'],name=node['name'],path=node['path'],hidden=node['hidden'],
            variantId=source['NodeVariantID'],status='savedConfiguration',
            brushLike=variant.get('BrushSize') is not None, parameters=params,unmappedFields=unmapped,
            displayParameterIds=show_ids,displayParameterMapping='unverifiedPrivateIds',
            note='非空字段包含关闭功能的保留值；并非全部在当前面板显示。配置值不能确认实时选中状态。'))
    return dict(schemaVersion=1,toolDatabase=str(tool_db),propertyDatabase=str(property_db),
        nodeCount=len(inventory['nodes']),subtoolCount=len(brushes),variantCount=len(variants),variantColumnCount=len(columns),
        propertyCount=len(definitions),brushLikeCount=sum(b.get('brushLike',False) for b in brushes),
        catalogDefinitionsWithoutColumnAssociation=[d['key'] for d in definitions if d['key'] not in FIELD_GROUPS],
        nonNullColumnUsage=dict(columns_used),unmappedColumnUsage=dict(missing),subtools=brushes,
        coverageBasis='All installed leaf nodes, their current saved variants and every non-null parameter column; no guessing of undocumented IDs.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--tool-db',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[3]
    sys.path.insert(0,str(root/'CSP_Shortcut_Manager'))
    from csp_shortcuts import read_tool_inventory
    result = audit(args.tool_db,Path(__file__).with_name('data')/'properties.sqlite3',read_tool_inventory(args.tool_db))
    args.output.mkdir(parents=True,exist_ok=True)
    (args.output/'configuration-audit.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    with (args.output/'subtool-coverage.csv').open('w',encoding='utf-8-sig',newline='') as f:
        writer = csv.writer(f)
        writer.writerow(['id','name','path','brush_like','saved_variant','parameters','associated','unmapped_count','unmapped_fields','visibility_mapping'])
        for b in result['subtools']:
            params=b.get('parameters',[])
            writer.writerow([b['id'],b['name'],' / '.join(b['path']),b.get('brushLike'),b.get('variantId'),len(params),
                sum(p['catalogStatus']=='present' for p in params),len(b.get('unmappedFields',[])),
                ' '.join(b.get('unmappedFields',[])),b.get('displayParameterMapping')])
    print(json.dumps({k:result[k] for k in ('nodeCount','subtoolCount','variantCount','variantColumnCount','propertyCount','brushLikeCount')},ensure_ascii=False))


if __name__ == '__main__':
    main()
