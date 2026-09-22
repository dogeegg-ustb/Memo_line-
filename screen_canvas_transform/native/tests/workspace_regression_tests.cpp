#include "sct/canvas_observe.hpp"
#include "wb/detector.hpp"
#include "wb/color.hpp"
#include "wb/features.hpp"
#include "wb/validate.hpp"
#include <cstdio>
#include <cmath>
#include <vector>
#include <fstream>
#include <cstdlib>

namespace {
int failures = 0;
void Check(bool ok, const char* name) {
  if (!ok) { ++failures; std::printf("FAIL: %s\n", name); }
}
void Fill(std::vector<uint8_t>& image, int w, wb::IntRect r, int value) {
  for (int y=r.top; y<r.bottom; ++y) for (int x=r.left; x<r.right; ++x) {
    auto* p=&image[(size_t(y)*w+x)*4];
    p[0]=p[1]=p[2]=uint8_t(value);p[3]=255;
  }
}
bool Near(wb::IntRect a, wb::IntRect b, int tolerance=1) {
  return std::abs(a.left-b.left)<=tolerance && std::abs(a.top-b.top)<=tolerance &&
         std::abs(a.right-b.right)<=tolerance && std::abs(a.bottom-b.bottom)<=tolerance;
}
void WorkspaceCase(wb::IntRect canvas, wb::IntRect user, const char* name, int scale=1) {
  int w=640*scale,h=480*scale;
  auto scaled=[&](wb::IntRect r) {return wb::IntRect{r.left*scale,r.top*scale,r.right*scale,r.bottom*scale};};
  const auto expected=scaled({80,60,560,420});
  std::vector<uint8_t> image(size_t(w)*h*4,255);
  Fill(image,w,{0,0,w,h},100);Fill(image,w,expected,45);Fill(image,w,scaled(canvas),240);
  wb::DetectionInput in;
  in.bgra=image.data();in.width=w;in.height=h;in.stride=w*4;
  in.user_roi=scaled(user);in.dpi_x=in.dpi_y=96;
  in.origin_x=-1920;in.origin_y=-100;in.capture_id="workspace-regression";
  auto out=wb::WorkspaceBorderDetector{}.Detect(in);
  std::printf("%s status=%s rect=[%d,%d,%d,%d] %s\n",name,wb::StatusName(out.status),
              out.workspace_capture.left,out.workspace_capture.top,
              out.workspace_capture.right,out.workspace_capture.bottom,out.message.c_str());
  Check(out.status==wb::Status::Ok,name);
  if(out.status==wb::Status::Ok) {
    Check(Near(out.workspace_capture,expected),"correct workspace outer bounds");
    Check(out.workspace_screen.left==out.workspace_capture.left-1920 &&
          out.workspace_screen.top==out.workspace_capture.top-100,"capture-to-screen origin");
    Check(wb::DeltaE76(out.background_model.center_lab,wb::BgrToLab(45,45,45))<1,
          "background model is workspace, not canvas");
  }
}
void Observations() {
  const int w=240,h=180;
  std::vector<uint8_t> image(size_t(w)*h*4,255);
  wb::BackgroundModel bg;bg.center_lab=wb::BgrToLab(45,45,45);
  bg.strong_delta_e=6;bg.weak_delta_e=12;
  auto observe=[&]() {return sct::ObserveCanvasExcludingBackground(image.data(),w,h,w*4,
      {0,0,w,h},-1920,80,bg);};
  for (int size:{2,4,12,40,140}) {
    Fill(image,w,{0,0,w,h},45);Fill(image,w,{30,20,30+size,20+size},240);
    auto o=observe();Check(o.four_sides_complete,"surrounded canvas independent of area");
    Check(Near(o.bounds_capture,{30,20,30+size,20+size},0),"small canvas exact bounds");
  }
  Fill(image,w,{0,0,w,h},45);Fill(image,w,{1,1,w-1,h-1},240);
  Check(observe().four_sides_complete,"one-pixel visible background rim is evidence");
  Fill(image,w,{0,0,w,h},45);
  for(int y=30;y<150;++y) for(int x=40;x<200;++x)
    if(std::abs(x-120)/80.0+std::abs(y-90)/60.0<1)
      Fill(image,w,{x,y,x+1,y+1},240);
  auto rotated=observe();
  Check(rotated.four_sides_complete,"rotated foreground is surrounded by background");
  Fill(image,w,{0,0,w,h},45);
  Fill(image,w,{20,20,80,70},240);Fill(image,w,{140,100,210,160},200);
  auto split=observe();
  Check(Near(split.bounds_capture,{140,100,210,160},0),"largest displayed foreground selected");
  Check(split.four_sides_complete,"largest foreground surrounded");
  Fill(image,w,{0,0,w,h},45);Fill(image,w,{20,20,w,160},240);
  Check(!observe().four_sides_complete,"clipped canvas has no right exterior evidence");
  Fill(image,w,{0,0,w,h},45);
  Check(observe().ambiguous,"uniform background does not invent canvas");
}
void RejectUnsupportedBoundaries() {
  constexpr int w=640,h=480;
  std::vector<uint8_t> image(size_t(w)*h*4,255);
  Fill(image,w,{0,0,w,h},100);Fill(image,w,{80,60,560,420},45);
  Fill(image,w,{200,130,440,350},240);
  wb::BackgroundModel bg;bg.center_lab=wb::BgrToLab(45,45,45);
  bg.strong_delta_e=6;bg.weak_delta_e=12;
  wb::DetectorConfig cfg;
  auto features=wb::ExtractFeatures(wb::BgraToBgr(wb::CopyBgraBuffer(image.data(),w,h,w*4)),cfg,1,nullptr);
  wb::Hypothesis hyp;hyp.grade=wb::EvidenceGrade::A;hyp.confidence=1;
  Check(wb::ValidateRectangle({80,60,560,420},hyp,features,bg,nullptr,cfg).ok,
        "independent validator accepts actual outer rectangle");
  Check(!wb::ValidateRectangle({80,60,560,390},hyp,features,bg,nullptr,cfg).ok,
        "three good edges cannot compensate for a missing bottom boundary");
  Check(!wb::ValidateRectangle({200,130,440,350},hyp,features,bg,nullptr,cfg).ok,
        "canvas inner boundary cannot be output as workspace");
  wb::DetectionInput in;in.bgra=image.data();in.width=w;in.height=h;in.stride=w*4;
  in.user_roi={240,180,400,300};in.capture_id="unsupported";
  Check(wb::WorkspaceBorderDetector{}.Detect(in).status!=wb::Status::Ok,
        "ROI containing only solid canvas cannot invent background");
  Fill(image,w,{0,0,w,h},45);
  Check(wb::WorkspaceBorderDetector{}.Detect(in).status!=wb::Status::Ok,
        "no separable outer boundary must fail");
}
}
int main(int argc, char** argv) {
  if(argc==9) {
    int w=std::atoi(argv[2]),h=std::atoi(argv[3]);
    if(w<=0 || h<=0 || w>16384 || h>16384)return 2;
    std::vector<uint8_t> image(size_t(w)*h*4);
    std::ifstream file(argv[1],std::ios::binary);
    if(!file.read(reinterpret_cast<char*>(image.data()),image.size()))return 2;
    wb::DetectionInput in;in.bgra=image.data();in.width=w;in.height=h;in.stride=w*4;
    in.user_roi={std::atoi(argv[4]),std::atoi(argv[5]),std::atoi(argv[6]),std::atoi(argv[7])};
    in.dpi_x=in.dpi_y=float(std::atof(argv[8]));in.capture_id="saved-capture-replay";
    auto out=wb::WorkspaceBorderDetector{}.Detect(in);
    std::printf("Replay status=%s rect=[%d,%d,%d,%d] grade=%d %s\n",wb::StatusName(out.status),
        out.workspace_capture.left,out.workspace_capture.top,out.workspace_capture.right,
        out.workspace_capture.bottom,int(out.grade),out.message.c_str());
    if(out.status==wb::Status::Ok) {
      auto obs=sct::ObserveCanvasExcludingBackground(image.data(),w,h,w*4,out.workspace_capture,
          0,0,out.background_model);
      std::printf("Canvas=[%d,%d,%d,%d] surrounded=%d ambiguous=%d\n",obs.bounds_capture.left,
          obs.bounds_capture.top,obs.bounds_capture.right,obs.bounds_capture.bottom,
          obs.four_sides_complete,obs.ambiguous);
      std::printf("Canvas boundary support=[%.3f,%.3f,%.3f,%.3f] visible=0x%x\n",
          obs.boundary_support[0],obs.boundary_support[1],obs.boundary_support[2],
          obs.boundary_support[3],obs.visible_edges_mask);
    }
    return out.status==wb::Status::Ok?0:1;
  }
  Observations();
  RejectUnsupportedBoundaries();
  for (auto roi:{wb::IntRect{70,50,570,430},wb::IntRect{95,75,545,405}}) {
    WorkspaceCase({310,230,330,250},roi,"small canvas");
    WorkspaceCase({200,130,440,350},roi,"medium canvas");
    WorkspaceCase({100,80,540,400},roi,"large canvas");
    WorkspaceCase({100,60,540,420},roi,"C-II vertical background bands");
    WorkspaceCase({80,80,560,400},roi,"C-II horizontal background bands");
    WorkspaceCase({100,80,560,420},roi,"L background");
  }
  WorkspaceCase({310,230,330,250},{95,75,545,405},"downsampled small canvas",3);
  std::printf("Workspace regression failures: %d\n",failures);
  return failures?1:0;
}
