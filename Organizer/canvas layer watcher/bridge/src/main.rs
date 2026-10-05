use std::{env, fs::{File, OpenOptions}, io::{BufWriter, Write}};
use clipfile::{ClipFile, Limits, RasterDataState};
use image::{ImageFormat, RgbaImage};
use serde_json::json;

fn main() {
    if let Err(error) = run() { eprintln!("{error}"); std::process::exit(1); }
}

// Only public clipfile APIs are used. The source CLIP is always read-only.
fn run() -> Result<(), Box<dyn std::error::Error>> {
    let args: Vec<_> = env::args_os().skip(1).collect();
    if args.len() != 2 && args.len() != 4 && args.len() != 6 {
        return Err("usage: clip-layer-bridge inspect <file.clip> | export/export-id <file.clip> <layer-name/layer-id> <new.png> [canvas-width-px canvas-height-px]".into());
    }
    let mode = args[0].to_string_lossy();
    let mut clip = ClipFile::open_with_limits(File::open(&args[1])?, Limits::default().with_max_raster_bytes(256_000_000))?;
    let document = clip.read_document()?;
    let database = clip.open_database()?;
    if mode == "inspect" && args.len() == 2 {
        let layers = document.layers().iter().map(|l| Ok(json!({
            "id":l.id(), "name":l.name(), "folder":l.is_folder(), "is_folder":l.is_folder(), "kind":l.kind().raw(),
            "uuid":layer_uuid(&database, l.id())?, "canvasId":l.canvas_id(), "opacity_raw":l.opacity(), "visible":l.is_visible()
        }))).collect::<Result<Vec<_>, Box<dyn std::error::Error>>>()?;
        println!("{}", json!({"layers": layers}));
        return Ok(());
    }
    if (mode != "export" && mode != "export-id") || (args.len() != 4 && args.len() != 6) { return Err("invalid arguments".into()); }
    let selector = args[2].to_str().ok_or("layer selector must be Unicode")?;
    let selected_id = if mode == "export-id" { Some(selector.parse::<i64>()?) } else { None };
    let matches: Vec<_> = document.layers().iter().filter(|l| match selected_id {
        Some(id) => l.id() == id, None => l.name() == Some(selector)
    }).collect();
    if matches.len() != 1 { return Err(format!("图层 {selector:?} 匹配 {} 个图层，无法唯一确定切换时的图层", matches.len()).into()); }
    let layer = matches[0];
    let name = layer.name().unwrap_or("");
    if layer.is_folder() { return Err("选中的是图层文件夹，无法作为单层渲染图像读取".into()); }
    let id = layer.id();
    let canvas = document.canvas(layer.canvas_id()).ok_or("图层没有有效的所属画布")?;
    // Recognizer's initialization supplies exact canvas pixels even for physical-unit CLIP files.
    // For pixel-unit files also cross-check the saved document's authoritative dimensions.
    let (width, height) = if args.len() == 6 {
        let width = args[4].to_str().ok_or("invalid width")?.parse::<u32>()?;
        let height = args[5].to_str().ok_or("invalid height")?.parse::<u32>()?;
        if width == 0 || height == 0 { return Err("画布像素尺寸必须大于零".into()); }
        if canvas.unit() == 0 && (pixel_dimension(canvas.width())?, pixel_dimension(canvas.height())?) != (width, height) {
            return Err("Recognizer 画布像素尺寸与保存的 CLIP 文档不一致，请重新校准录制".into());
        }
        (width, height)
    } else {
        if canvas.unit() != 0 { return Err("非像素单位文档必须提供 Recognizer 校准的完整画布像素尺寸".into()); }
        (pixel_dimension(canvas.width())?, pixel_dimension(canvas.height())?)
    };
    if u64::from(width) * u64::from(height) > 64_000_000 { return Err("完整画布超过 6400 万像素显示上限".into()); }
    let (offset_x, offset_y) = layer_offset(&database, id)?;
    let source = database.layer_raster_source(id)?.ok_or("该图层没有可读取的 render raster")?;
    let raster = clip.decode_raster(&database, &source)?;
    if matches!(raster.data_state(), RasterDataState::MissingExternalChunk) {
        return Err("图层引用的像素块缺失，不能将默认填充当作实际图像".into());
    }
    let (raster_width, raster_height) = (raster.width(), raster.height());
    let format = format!("{:?}", raster.format());
    let data_state = format!("{:?}", raster.data_state());
    let mut output = BufWriter::new(OpenOptions::new().write(true).create_new(true).open(&args[3])?);
    // Keep the full canvas extent even when only a small region has nontransparent pixels.
    let canvas_image = place_on_canvas(raster.into_dynamic_image().into_rgba8(), width, height, offset_x, offset_y);
    canvas_image.write_to(&mut output, ImageFormat::Png)?;
    output.flush()?;
    println!("{}", json!({"layerId":id,"layerName":name,"layerUuid":layer_uuid(&database, id)?,"width":width,"height":height,
        "pixelFormat":format,"dataState":data_state,"canvasId":canvas.id(),"fullCanvas":true,
        "rasterWidth":raster_width,"rasterHeight":raster_height,"offsetX":offset_x,"offsetY":offset_y}));
    Ok(())
}

fn layer_uuid(database: &clipfile::Database, id: i64) -> Result<Option<String>, Box<dyn std::error::Error>> {
    if !database.schema().has_column("Layer", "LayerUuid") { return Ok(None); }
    // Match Recognizer's layer property core: BLOB UUIDs are lowercase hex,
    // TEXT UUIDs keep their stored spelling.
    Ok(database.connection().query_row(
        "SELECT CASE WHEN typeof(LayerUuid)='blob' THEN lower(hex(LayerUuid)) ELSE CAST(LayerUuid AS TEXT) END FROM Layer WHERE MainId=?1",
        [id], |row| row.get(0))?)
}

fn pixel_dimension(value: f64) -> Result<u32, Box<dyn std::error::Error>> {
    if !value.is_finite() || value <= 0.0 || value > u32::MAX as f64 || value.fract() != 0.0 {
        return Err("完整画布尺寸必须是有效的整数像素值".into());
    }
    Ok(value as u32)
}

fn layer_offset(database: &clipfile::Database, id: i64) -> Result<(i64, i64), Box<dyn std::error::Error>> {
    if !database.schema().has_column("Layer", "LayerOffsetX") || !database.schema().has_column("Layer", "LayerOffsetY") {
        return Err("缺少图层偏移信息，无法确认图层在完整画布上的位置".into());
    }
    let (x, y): (f64, f64) = database.connection().query_row(
        "SELECT LayerOffsetX, LayerOffsetY FROM Layer WHERE MainId = ?1", [id],
        |row| Ok((row.get(0)?, row.get(1)?)))?;
    if !x.is_finite() || !y.is_finite() || x.abs() > i32::MAX as f64 || y.abs() > i32::MAX as f64 || x.fract() != 0.0 || y.fract() != 0.0 {
        return Err("图层有非整数偏移，当前无法无损映射到完整画布像素".into());
    }
    Ok((x as i64, y as i64))
}

fn place_on_canvas(source: RgbaImage, width: u32, height: u32, x: i64, y: i64) -> RgbaImage {
    if source.dimensions() == (width, height) && x == 0 && y == 0 { return source; }
    let mut canvas = RgbaImage::new(width, height);
    // Copy the intersecting rows exactly: no rescaling, alpha blend, or content bounding-box crop.
    let left = 0i64.max(x); let top = 0i64.max(y);
    let right = i64::from(width).min(x + i64::from(source.width()));
    let bottom = i64::from(height).min(y + i64::from(source.height()));
    for target_y in top..bottom {
        if right <= left { break; }
        let source_start = (((target_y - y) as usize) * source.width() as usize + (left - x) as usize) * 4;
        let target_start = (target_y as usize * width as usize + left as usize) * 4;
        let bytes = (right - left) as usize * 4;
        canvas.as_mut()[target_start..target_start + bytes].copy_from_slice(&source.as_raw()[source_start..source_start + bytes]);
    }
    canvas
}

#[cfg(test)]
mod tests {
    use super::*;
    use clipfile::{PixelFormat, RasterImage};

    #[test]
    fn full_canvas_extent_and_offsets() {
        let source = RgbaImage::from_pixel(2, 2, image::Rgba([20, 40, 80, 128]));
        let canvas = place_on_canvas(source.clone(), 8, 6, 3, 2);
        assert_eq!(canvas.dimensions(), (8, 6));
        assert_eq!(*canvas.get_pixel(0, 0), image::Rgba([0, 0, 0, 0]));
        assert_eq!(*canvas.get_pixel(3, 2), image::Rgba([20, 40, 80, 128]));
        assert_eq!(*canvas.get_pixel(7, 5), image::Rgba([0, 0, 0, 0]));
        let canvas = place_on_canvas(source, 8, 6, -1, -1);
        assert_eq!(*canvas.get_pixel(0, 0), image::Rgba([20, 40, 80, 128]));
        assert_eq!(canvas.dimensions(), (8, 6));
    }

    #[test]
    fn native_tile_pixels_round_trip() -> Result<(), Box<dyn std::error::Error>> {
        let fixture = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../../../replayer/status change/csp_blank_template.clip");
        let output_dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../checks/artifacts");
        std::fs::create_dir_all(&output_dir)?;
        let output = output_dir.join("colored-fixture.clip");
        if output.exists() { std::fs::remove_file(&output)?; }
        let mut clip = ClipFile::open(File::open(fixture)?)?;
        let database = clip.open_database()?;
        let source = database.layer_raster_source(3)?.ok_or("fixture layer missing")?;
        let raster = clip.decode_raster(&database, &source)?;
        let (w, h) = (raster.width(), raster.height());
        let mut pixels = vec![0u8; (w as usize) * (h as usize) * 4];
        for (i, pixel) in pixels.chunks_exact_mut(4).enumerate() {
            pixel.copy_from_slice(&[32, 128, 240, if i % 2 == 0 { 192 } else { 0 }]);
        }
        let image = RasterImage::from_pixels(w, h, PixelFormat::Rgba8, pixels.clone())?;
        let mut writer = clip.writer()?;
        let id = writer.clone_raster_layer_from_template_image(3, 2, "测试图层 α", image, Limits::default())?;
        writer.write_to_path(&output)?;
        let mut reopened = ClipFile::open(File::open(&output)?)?;
        let database = reopened.open_database()?;
        let source = database.layer_raster_source(id)?.ok_or("new layer missing")?;
        let decoded = reopened.decode_raster(&database, &source)?;
        assert_eq!(decoded.pixels(), pixels.as_slice());
        assert_eq!((decoded.width(), decoded.height()), (w, h));
        Ok(())
    }
}
