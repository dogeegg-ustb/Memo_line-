#include "wb/color.hpp"
#include "wb/detector.hpp"
#include "sct/canvas_observe.hpp"

#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <vector>

namespace {

constexpr int kWidth = 420;
constexpr int kHeight = 480;
constexpr wb::IntRect kRoi{3, 16, 415, 470};

void Fill(std::vector<uint8_t>& image, wb::IntRect rect, uint8_t gray) {
  for (int y = rect.top; y < rect.bottom; ++y) {
    for (int x = rect.left; x < rect.right; ++x) {
      auto* pixel = image.data() + (size_t(y) * kWidth + x) * 4;
      pixel[0] = pixel[1] = pixel[2] = gray;
      pixel[3] = 255;
    }
  }
}

wb::DetectionOutput Detect(std::vector<uint8_t>& image) {
  wb::BackgroundModel background;
  background.center_lab = wb::BgrToLab(45, 45, 45);
  background.strong_delta_e = 3.5f;
  background.weak_delta_e = 6.f;
  wb::DetectionInput input;
  input.bgra = image.data();
  input.width = kWidth;
  input.height = kHeight;
  input.stride = kWidth * 4;
  input.user_roi = kRoi;
  input.dpi_x = input.dpi_y = 144.f;
  input.capture_id = "navigator-orientation-regression";
  return wb::DetectNavigatorThumbnailCii(input, background);
}

bool Covers(wb::IntRect outer, wb::IntRect canvas, int tolerance = 1) {
  return outer.left <= canvas.left + tolerance && outer.top <= canvas.top + tolerance &&
         outer.right >= canvas.right - tolerance && outer.bottom >= canvas.bottom - tolerance;
}

bool ExpectCase(const char* name, std::vector<uint8_t>& image, wb::IntRect canvas) {
  const auto result = Detect(image);
  std::printf("%s: %s [%d,%d,%d,%d] %s\n", name, wb::StatusName(result.status),
              result.workspace_capture.left, result.workspace_capture.top,
              result.workspace_capture.right, result.workspace_capture.bottom,
              result.message.c_str());
  return result.status == wb::Status::Ok && Covers(result.workspace_capture, canvas);
}

bool RedLinesDoNotChangeThumbnail() {
  std::vector<uint8_t> image(size_t(kWidth)*kHeight*4);
  Fill(image,{0,0,kWidth,kHeight},45);
  Fill(image,{5,87,413,399},255);
  const auto clean=Detect(image);
  if (clean.status!=wb::Status::Ok) return false;
  const auto original=image;
  for(int y=120;y<360;++y) for(int x=80;x<340;++x) {
    auto* p=image.data()+(size_t(y)*kWidth+x)*4;
    p[0]=p[1]=20;p[2]=220;
  }
  const auto artwork=Detect(image);
  if(artwork.status!=wb::Status::Ok || !Covers(artwork.workspace_capture,{5,87,413,399})) return false;
  for (int red : {255,100}) for (int thickness : {1,3}) {
    image=original;
    for (int top : {kRoi.top,36,424,kRoi.bottom-thickness}) {
      for (int y=top;y<top+thickness;++y) for (int x=kRoi.left;x<kRoi.right;++x) {
        auto* p=image.data()+(size_t(y)*kWidth+x)*4;
        p[0]=p[1]=40;p[2]=uint8_t(red);
      }
      // Faint antialiasing beside the saturated line must also be ignored.
      for (int y : {top-1,top+thickness}) {
        if (y<kRoi.top || y>=kRoi.bottom) continue;
        for (int x=kRoi.left;x<kRoi.right;++x) {
          auto* p=image.data()+(size_t(y)*kWidth+x)*4;
          p[0]=p[1]=45;p[2]=65;
        }
      }
    }
    const auto before=image;
    const auto out=Detect(image);
    const auto a=out.workspace_capture,b=clean.workspace_capture;
    std::printf("red lines r=%d width=%d: %s [%d,%d,%d,%d]\n",red,thickness,
        wb::StatusName(out.status),a.left,a.top,a.right,a.bottom);
    if (out.status!=wb::Status::Ok || a.left!=b.left || a.top!=b.top ||
        a.right!=b.right || a.bottom!=b.bottom || image!=before) return false;
  }
  return true;
}

}  // namespace

int main(int argc,char** argv) {
  if (argc==10) {
    const int w=std::atoi(argv[2]),h=std::atoi(argv[3]);
    if(w<=0 || h<=0 || w>16384 || h>16384) return 2;
    std::vector<uint8_t> image(size_t(w)*h*4);
    std::ifstream input(argv[1],std::ios::binary);
    if(!input.read(reinterpret_cast<char*>(image.data()),image.size())) return 2;
    wb::BackgroundModel bg;const int gray=std::atoi(argv[9]);
    bg.center_lab=wb::BgrToLab(gray,gray,gray);bg.strong_delta_e=3.5f;bg.weak_delta_e=6.f;
    wb::DetectionInput in;in.bgra=image.data();in.width=w;in.height=h;in.stride=w*4;
    in.user_roi={std::atoi(argv[4]),std::atoi(argv[5]),std::atoi(argv[6]),std::atoi(argv[7])};
    in.dpi_x=in.dpi_y=float(std::atof(argv[8]));
    const auto out=wb::DetectNavigatorThumbnailCii(in,bg);
    std::printf("Thumbnail %s [%d,%d,%d,%d] %s\n",wb::StatusName(out.status),
        out.workspace_capture.left,out.workspace_capture.top,out.workspace_capture.right,
        out.workspace_capture.bottom,out.message.c_str());
    if(out.status!=wb::Status::Ok) return 1;
    const auto paper=sct::ObserveCanvasExcludingBackground(image.data(),w,h,w*4,
        out.workspace_capture,0,0,bg,1.5f,true,4961,7016);
    std::printf("Paper [%d,%d,%d,%d] ambiguous=%d %s\n",paper.bounds_capture.left,
        paper.bounds_capture.top,paper.bounds_capture.right,paper.bounds_capture.bottom,
        paper.ambiguous,paper.ambiguity_reason);
    return paper.ambiguous?1:0;
  }
  int failures = 0;
  if (!RedLinesDoNotChangeThumbnail()) ++failures;
  std::vector<uint8_t> image(size_t(kWidth) * kHeight * 4);
  constexpr wb::IntRect horizontal_canvas{60, 16, 360, 470};
  constexpr wb::IntRect vertical_canvas{3, 87, 415, 399};

  Fill(image, {0, 0, kWidth, kHeight}, 45);
  Fill(image, horizontal_canvas, 255);
  if (!ExpectCase("left-right-background", image, horizontal_canvas)) ++failures;

  Fill(image, {0, 0, kWidth, kHeight}, 45);
  Fill(image, vertical_canvas, 255);
  if (!ExpectCase("top-bottom-background", image, vertical_canvas)) ++failures;

  Fill(image, {0, 0, kWidth, kHeight}, 45);
  Fill(image, {5, 87, 413, 399}, 255);
  if (!ExpectCase("top-bottom-connected-by-side-rim", image, {5, 87, 413, 399})) ++failures;

  Fill(image, {0, 0, kWidth, kHeight}, 45);
  const auto no_canvas = Detect(image);
  if (no_canvas.status == wb::Status::Ok) ++failures;

  Fill(image, {0, 0, kWidth, kHeight}, 255);
  Fill(image, {kRoi.left, kRoi.top, kRoi.right, 87}, 45);
  const auto one_band = Detect(image);
  if (one_band.status == wb::Status::Ok) ++failures;

  std::printf("navigator orientation regression failures=%d\n", failures);
  return failures == 0 ? 0 : 1;
}
