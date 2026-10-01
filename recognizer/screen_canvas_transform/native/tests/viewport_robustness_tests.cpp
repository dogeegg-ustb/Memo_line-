#include "sct/canvas_observe.hpp"
#include "sct/viewport_frame.hpp"
#include "sct/workspace_canvas_relation.hpp"
#include "wb/color.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <vector>

namespace {
int failures=0;
void Check(bool ok,const char* name) {
  if(!ok) { ++failures;std::printf("FAIL: %s\n",name); }
}
void Pixel(std::vector<uint8_t>& image,int w,int x,int y,int b,int g,int r) {
  auto* p=&image[(size_t(y)*w+x)*4];p[0]=uint8_t(b);p[1]=uint8_t(g);p[2]=uint8_t(r);p[3]=255;
}
void Fill(std::vector<uint8_t>& image,int w,wb::IntRect rect,int value) {
  for(int y=rect.top;y<rect.bottom;++y) for(int x=rect.left;x<rect.right;++x)
    Pixel(image,w,x,y,value,value,value);
}
void Line(std::vector<uint8_t>& image,int w,int h,sct::Vec2 a,sct::Vec2 b) {
  const int steps=std::max(1,int(std::ceil(std::hypot(b.x-a.x,b.y-a.y)*2)));
  for(int i=0;i<=steps;++i) {
    const double t=double(i)/steps;
    const int x=int(std::lround(a.x+(b.x-a.x)*t)),y=int(std::lround(a.y+(b.y-a.y)*t));
    if(x>=0 && y>=0 && x<w && y<h) Pixel(image,w,x,y,0,0,255);
  }
}
void Rectangle(std::vector<uint8_t>& image,int w,int h,int l,int t,int r,int b) {
  const sct::Vec2 p[]={{double(l),double(t)},{double(r),double(t)},
                     {double(r),double(b)},{double(l),double(b)}};
  for(int i=0;i<4;++i) Line(image,w,h,p[i],p[(i+1)%4]);
}
sct::ViewportCompletionInput WideViewportInput(std::vector<uint8_t>& image,int w,int h) {
  sct::ViewportCompletionInput in;
  in.bgra=image.data();in.width=w;in.height=h;in.stride=w*4;
  in.thumbnail_roi={37,28,w,360};in.navigator_canvas_bounds={93,29,326,359};
  in.display_rotation_confidence=1;
  auto& rel=in.workspace_canvas_relation;
  rel.workspace_roi={0,0,1324,960};
  rel.visible_canvas_bounds_workspace_local={196,0,1128,960};
  rel.full_canvas_model_workspace_local={196,-16,1128,1304};
  rel.canvas_crop_sides=2|8;rel.confidence=0.9f;
  rel.canvas_aspect_ratio=233.f/330;
  rel.visible_canvas_fraction_x=1;rel.visible_canvas_fraction_y=960.f/1320;
  rel.visible_canvas_workspace_fraction_x=932.f/1324;rel.visible_canvas_workspace_fraction_y=1;
  return in;
}
void WideViewportWithArtwork() {
  constexpr int w=379,h=363;
  std::vector<uint8_t> image(size_t(w)*h*4,255);
  Fill(image,w,{0,0,w,h},41);Fill(image,w,{93,29,326,359},255);
  Rectangle(image,w,h,44,33,375,273);
  Rectangle(image,w,h,145,150,205,205);
  Line(image,w,h,{120,180},{290,180});
  Line(image,w,h,{165,100},{165,260});
  Line(image,w,h,{155,140},{280,175});
  auto out=sct::CompleteViewportFrame(WideViewportInput(image,w,h));
  std::printf("wide viewport: status=%d %s size=%.2fx%.2f\n",int(out.status),out.message,out.frame.width,out.frame.height);
  Check(out.status==sct::FailStatus::Ok,"wide viewport survives red artwork and different paper aspect");
  if(out.status!=sct::FailStatus::Ok) return;
  Check(std::abs(out.frame.width-331)<3 && std::abs(out.frame.height-240)<3,"viewport may extend beyond paper width");
  Check(std::hypot(out.frame.origin_top_left_displayed.x-44.5,
                   out.frame.origin_top_left_displayed.y-33.5)<3,"select actual outer frame, not handwriting");
  Check(out.frame.complete_edge_export_count==4,"four real corners retain four complete edges");
}
void SeparateRectanglesStayAmbiguous() {
  constexpr int w=240,h=190;
  std::vector<uint8_t> image(size_t(w)*h*4,255);
  Rectangle(image,w,h,20,20,100,80);Rectangle(image,w,h,130,100,210,160);
  // A line sharing a projection with both frames must not make the first
  // greedy subset the sole candidate.
  Line(image,w,h,{30,88},{200,88});
  sct::ViewportCompletionInput in;
  in.bgra=image.data();in.width=w;in.height=h;in.stride=w*4;
  in.thumbnail_roi={0,0,w,h};in.navigator_canvas_bounds={0,0,w,h};
  in.workspace_canvas_relation.workspace_roi={0,0,800,600};
  in.workspace_canvas_relation.visible_canvas_fraction_x=80.f/w;
  in.workspace_canvas_relation.visible_canvas_fraction_y=60.f/h;
  auto out=sct::CompleteViewportFrame(in);
  Check(out.status==sct::FailStatus::AmbiguousViewportGeometry,"equally plausible independent frames must remain ambiguous");
}
sct::ViewportCompletionInput FullyCroppedInput(std::vector<uint8_t>& image,int w,int h) {
  sct::ViewportCompletionInput in;
  in.bgra=image.data();in.width=w;in.height=h;in.stride=w*4;
  in.thumbnail_roi={13,31,w,362};in.navigator_canvas_bounds={67,31,300,362};
  in.display_rotation_confidence=.85f;
  auto& rel=in.workspace_canvas_relation;
  rel.workspace_roi={0,0,1765,1282};
  rel.visible_canvas_bounds_workspace_local=rel.workspace_roi;
  rel.full_canvas_model_workspace_local={0,0,1765,2496};
  rel.canvas_crop_sides=1|2|4|8;rel.confidence=.5f;rel.canvas_aspect_ratio=.7071f;
  rel.visible_canvas_workspace_fraction_x=rel.visible_canvas_workspace_fraction_y=1;
  return in;
}
void CompleteFrameOutranksUnanchoredArtwork() {
  constexpr int w=354,h=363;
  std::vector<uint8_t> image(size_t(w)*h*4,255);
  Fill(image,w,{0,0,w,h},41);Fill(image,w,{67,31,300,362},255);
  Rectangle(image,w,h,13,49,314,268);
  // This separate handwritten vertical is the same pure red as the frame.
  // With all paper axes cropped, no independent theoretical size exists;
  // pattern 0.1 manufactures a 70x51 rectangle with the correct aspect.
  Line(image,w,h,{177,156},{177,207});
  auto out=sct::CompleteViewportFrame(FullyCroppedInput(image,w,h));
  std::printf("closed frame vs fragment: status=%d %s\n",int(out.status),out.message);
  Check(out.status==sct::FailStatus::Ok,"four observed corners outrank an aspect-constructed handwriting rectangle");
  if(out.status==sct::FailStatus::Ok) {
    Check(out.frame.complete_edge_export_count==4,"closed frame keeps all complete edges when handwriting shares its chroma");
    Check(std::hypot(out.frame.origin_top_left_displayed.x-13.5,
                     out.frame.origin_top_left_displayed.y-49.5)<2 &&
          std::abs(out.frame.width-301)<2 && std::abs(out.frame.height-219)<2,
          "select factual closed frame instead of a smaller constructed rectangle");
  }
}
void ClippedFrameOutranksCornerAndFragmentArtwork() {
  constexpr int w=381,h=525;
  std::vector<uint8_t> image(size_t(w)*h*4,255);
  Fill(image,w,{0,0,w,h},41);Fill(image,w,{93,101,326,432},255);
  // Three sides, with the bottom outside the thumbnail. The top is a
  // complete measured side; the two upright arms prove both top corners.
  Line(image,w,h,{42,235},{343,235});
  Line(image,w,h,{42,235},{42,431});Line(image,w,h,{343,235},{343,431});
  Line(image,w,h,{190,330},{217,330});
  Line(image,w,h,{150,145},{200,145});Line(image,w,h,{150,145},{150,185});
  auto in=FullyCroppedInput(image,w,h);
  in.thumbnail_roi={37,101,w,432};in.navigator_canvas_bounds={93,101,326,432};
  auto out=sct::CompleteViewportFrame(in);
  std::printf("clipped frame vs artwork: status=%d %s\n",int(out.status),out.message);
  Check(out.status==sct::FailStatus::Ok,"clipped U with a measured complete side outranks fragment and L-shaped artwork");
  if(out.status==sct::FailStatus::Ok)
    Check(std::hypot(out.frame.origin_top_left_displayed.x-42.5,
                     out.frame.origin_top_left_displayed.y-235.5)<2 &&
          std::abs(out.frame.width-301)<2 && std::abs(out.frame.height-301.*1282/1765)<2,
          "clipped frame uses the measured top width to recover the missing bottom");
}
void SeparateClippedFramesStayAmbiguous() {
  constexpr int w=300,h=280;
  std::vector<uint8_t> image(size_t(w)*h*4,255);
  for(int l:{20,175}) {
    Line(image,w,h,{double(l),20},{double(l+80),20});
    Line(image,w,h,{double(l),20},{double(l),55});
    Line(image,w,h,{double(l+80),20},{double(l+80),55});
  }
  sct::ViewportCompletionInput in;
  in.bgra=image.data();in.width=w;in.height=h;in.stride=w*4;
  in.thumbnail_roi={0,0,w,h};in.navigator_canvas_bounds={0,0,w,h};
  in.display_rotation_confidence=1;
  in.workspace_canvas_relation.workspace_roi={0,0,800,600};
  auto out=sct::CompleteViewportFrame(in);
  Check(out.status==sct::FailStatus::AmbiguousViewportGeometry,"two equally anchored U-shaped frames remain ambiguous");
}
void SeparateUnanchoredFragmentsStayAmbiguous() {
  constexpr int w=354,h=363;
  std::vector<uint8_t> image(size_t(w)*h*4,255);
  Line(image,w,h,{100,150},{100,201});Line(image,w,h,{200,230},{200,281});
  auto out=sct::CompleteViewportFrame(FullyCroppedInput(image,w,h));
  Check(out.status==sct::FailStatus::AmbiguousViewportGeometry,"without a complete edge two fragment-based hypotheses stay ambiguous");
}
void PaleSidesRecoverFromAdjacentEdges() {
  constexpr int w=328,h=448;
  std::vector<uint8_t> image(size_t(w)*h*4,255);
  Fill(image,w,{0,0,w,h},41);Fill(image,w,{43,112,274,442},255);
  constexpr int l=87,t=128,r=292,b=277;
  for (int x=l;x<=r;++x) {
    Pixel(image,w,x,t,20,20,206);
    // Saturation 0.255 fails the narrow seed threshold, but this is an
    // actual continuous thin red stroke. The neighbor's endpoint locates it.
    Pixel(image,w,x,b,137,137,184);
  }
  for (int y=t;y<=b;++y) {
    Pixel(image,w,l,y,137,137,184);Pixel(image,w,r,y,3,3,189);
  }
  auto in=FullyCroppedInput(image,w,h);
  in.thumbnail_roi={0,112,w,442};in.navigator_canvas_bounds={43,112,274,442};
  auto out=sct::CompleteViewportFrame(in);
  std::printf("pale complete sides: status=%d %s complete=%d\n",int(out.status),out.message,
              out.frame.complete_edge_export_count);
  Check(out.status==sct::FailStatus::Ok && out.frame.complete_edge_export_count==4,
        "complete pale sides lacking strict Hough seeds recover with independent pixel evidence");
  if(out.status==sct::FailStatus::Ok)
    Check(std::hypot(out.frame.origin_top_left_displayed.x-l-.5,
                     out.frame.origin_top_left_displayed.y-t-.5)<2 &&
          std::abs(out.frame.width-(r-l))<2 && std::abs(out.frame.height-(b-t))<2,
          "weak-side recovery uses actual corners instead of the initial fragment-scale estimate");
}
void CurvedRedStrokesAreRejected() {
  constexpr int w=320,h=300;
  constexpr double pi=3.14159265358979323846;
  for(double amplitude:{1.5,3.,5.}) {
    std::vector<uint8_t> image(size_t(w)*h*4,255);
    for(int x=40;x<=280;++x) {
      const int bend=int(std::lround(amplitude*std::sin((x-40)*pi/240)));
      Pixel(image,w,x,60+bend,0,0,255);Pixel(image,w,x,220+bend,0,0,255);
    }
    for(int y=60;y<=220;++y) {
      const int bend=int(std::lround(amplitude*std::sin((y-60)*pi/160)));
      Pixel(image,w,40+bend,y,0,0,255);Pixel(image,w,280+bend,y,0,0,255);
    }
    sct::ViewportCompletionInput in;
    in.bgra=image.data();in.width=w;in.height=h;in.stride=w*4;
    in.thumbnail_roi={0,0,w,h};in.navigator_canvas_bounds={0,0,w,h};
    in.display_rotation_confidence=1;
    in.workspace_canvas_relation.workspace_roi={0,0,1200,800};
    in.workspace_canvas_relation.visible_canvas_bounds_workspace_local={0,0,1200,800};
    in.workspace_canvas_relation.canvas_crop_sides=15;
    auto out=sct::CompleteViewportFrame(in);
    std::printf("bowed strokes %.1fpx: status=%d %s\n",amplitude,int(out.status),out.message);
    Check(out.status!=sct::FailStatus::Ok,"bowed near-rectangular strokes are not very straight red frame edges");
  }
}
void StraightFrameSurvivesNearbyRedMark() {
  constexpr int w=381,h=525;
  std::vector<uint8_t> image(size_t(w)*h*4,255);
  Fill(image,w,{0,0,w,h},41);Fill(image,w,{93,101,326,432},255);
  for(int x=42;x<=343;++x) for(int y:{235,236}) Pixel(image,w,x,y,20,20,206);
  for(int y=235;y<432;++y) {
    for(int x:{42,43,342,343}) Pixel(image,w,x,y,3,3,189);
  }
  // A tiny saturated mark next to the top side changes its local profile
  // center. Most of the measured 301-pixel side is still exactly straight.
  for(int x=186;x<=188;++x) Pixel(image,w,x,234,0,0,255);
  auto in=FullyCroppedInput(image,w,h);
  in.thumbnail_roi={37,101,w,432};in.navigator_canvas_bounds={93,101,326,432};
  auto out=sct::CompleteViewportFrame(in);
  Check(out.status==sct::FailStatus::Ok && out.frame.complete_edge_export_count>=1,
        "a few overlapping drawing pixels do not reject an otherwise straight complete side");
  if(out.status==sct::FailStatus::Ok)
    Check(std::abs(out.frame.width-300)<2 && std::abs(out.frame.height-300.*1282/1765)<2,
          "local profile contamination does not change the real U-frame dimensions");
}
void WideViewportWithSeparateRedGroup() {
  constexpr int w=379,h=363;
  std::vector<uint8_t> image(size_t(w)*h*4,255);
  Fill(image,w,{0,0,w,h},41);Fill(image,w,{93,29,326,359},255);
  Rectangle(image,w,h,44,25,375,265);
  // This small artwork group is separate from the viewport, forcing the
  // multi-group shape gate instead of the former singleton bypass.
  Rectangle(image,w,h,140,300,170,325);
  auto in=WideViewportInput(image,w,h);
  in.workspace_canvas_relation.canvas_crop_sides=8;
  in.workspace_canvas_relation.visible_canvas_bounds_workspace_local={196,16,1128,960};
  in.workspace_canvas_relation.full_canvas_model_workspace_local={196,16,1128,1336};
  in.workspace_canvas_relation.visible_canvas_fraction_y=944.f/1320;
  auto out=sct::CompleteViewportFrame(in);
  std::printf("separate artwork group: status=%d %s\n",int(out.status),out.message);
  Check(out.status==sct::FailStatus::Ok,"multi-group validation uses workspace extent and aspect instead of paper shape");
  if(out.status==sct::FailStatus::Ok)
    Check(std::abs(out.frame.width-331)<3 && std::abs(out.frame.height-240)<3,
          "multi-group shape validation retains larger-than-paper viewport");
}
void CrossingStrokesAreNotCorners() {
  constexpr int w=200,h=180;
  std::vector<uint8_t> image(size_t(w)*h*4,255);
  Line(image,w,h,{20,70},{170,70});Line(image,w,h,{80,10},{80,160});
  sct::ViewportCompletionInput in;
  in.bgra=image.data();in.width=w;in.height=h;in.stride=w*4;
  in.thumbnail_roi={0,0,w,h};in.navigator_canvas_bounds={0,0,w,h};
  in.workspace_canvas_relation.workspace_roi={0,0,800,600};
  auto out=sct::CompleteViewportFrame(in);
  Check(out.status!=sct::FailStatus::Ok,"interior cross of handwriting is not a viewport corner");
}
void PaperAspectDoesNotFilterViewport() {
  constexpr int w=200,h=160;
  std::vector<uint8_t> image(size_t(w)*h*4,200);
  Rectangle(image,w,h,20,25,110,95);Rectangle(image,w,h,140,30,170,55);
  sct::ViewportCompletionInput in;
  in.bgra=image.data();in.width=w;in.height=h;in.stride=w*4;
  in.thumbnail_roi={5,5,195,155};in.navigator_canvas_bounds={10,10,130,150};
  auto& rel=in.workspace_canvas_relation;
  rel.workspace_roi={0,0,900,700};rel.canvas_aspect_ratio=120.f/140;
  // Standalone callers provide normalized expected viewport dimensions.
  rel.visible_canvas_fraction_x=90.f/120;rel.visible_canvas_fraction_y=70.f/140;
  rel.visible_canvas_workspace_fraction_x=rel.visible_canvas_workspace_fraction_y=1;
  auto out=sct::CompleteViewportFrame(in);
  std::printf("paper/viewport aspect: status=%d %s\n",int(out.status),out.message);
  Check(out.status==sct::FailStatus::Ok,"paper aspect cannot reject a matching workspace viewport");
  if(out.status==sct::FailStatus::Ok)
    Check(std::abs(out.frame.width-90)<3 && std::abs(out.frame.height-70)<3,
          "select viewport according to workspace shape among two red rectangles");
}
void SingleWrongAspectIsRejected() {
  constexpr int w=200,h=180;
  std::vector<uint8_t> image(size_t(w)*h*4,255);
  Rectangle(image,w,h,40,40,120,120);
  sct::ViewportCompletionInput in;
  in.bgra=image.data();in.width=w;in.height=h;in.stride=w*4;
  in.thumbnail_roi={0,0,w,h};in.navigator_canvas_bounds={0,0,w,h};
  in.workspace_canvas_relation.workspace_roi={0,0,1600,600};
  auto out=sct::CompleteViewportFrame(in);
  Check(out.status==sct::FailStatus::AmbiguousViewportGeometry,"singleton red artwork cannot bypass workspace aspect validation");
}
void DiagonalDirectionFamilies() {
  constexpr int w=320,h=300;
  for(double angle:{45.,135.,-45.,-135.}) {
    std::vector<uint8_t> image(size_t(w)*h*4,255);
    const double rad=-angle*3.14159265358979323846/180;
    auto point=[&](double x,double y) { return sct::Vec2{160+std::cos(rad)*x-std::sin(rad)*y,
                                                       150+std::sin(rad)*x+std::cos(rad)*y}; };
    sct::Vec2 corners[]={point(-60,-40),point(60,-40),point(60,40),point(-60,40)};
    for(int i=0;i<4;++i) Line(image,w,h,corners[i],corners[(i+1)%4]);
    sct::ViewportCompletionInput in;
    in.bgra=image.data();in.width=w;in.height=h;in.stride=w*4;
    in.thumbnail_roi={0,0,w,h};in.navigator_canvas_bounds={10,10,310,290};
    in.display_rotation_degrees=float(angle);in.display_rotation_confidence=1;
    in.workspace_canvas_relation.workspace_roi={0,0,900,600};
    auto out=sct::CompleteViewportFrame(in);
    Check(out.status==sct::FailStatus::Ok,"diagonal family classification does not depend on screen-axis tie");
    if(out.status==sct::FailStatus::Ok)
      Check(std::abs(out.frame.width-120)<3 && std::abs(out.frame.height-80)<3,"diagonal rectangle physical dimensions");
  }
}
void ClippedPaperModelKeepsExtent() {
  for(bool top:{false,true}) {
    sct::WorkspaceCanvasRelationInput in;
    in.canvas_pixel_width=1000;in.canvas_pixel_height=2000;
    in.workspace_roi_screen={0,0,800,600};
    in.workspace_canvas.bounds_screen=top ? wb::IntRect{200,0,600,450} : wb::IntRect{200,150,600,600};
    in.workspace_canvas.confidence=1;in.workspace_canvas.visible_edges_mask=top ? 1|4|8 : 1|2|4;
    auto out=sct::BuildWorkspaceCanvasRelation(in);
    Check(out.status==sct::FailStatus::Ok,"clipped paper relation builds");
    const auto full=out.relation.full_canvas_model_workspace_local;
    Check(full.width()==400 && full.height()==800,"clipping cannot overwrite inferred paper dimensions");
    Check(top ? full.bottom==450 && full.top==-350 : full.top==150 && full.bottom==950,
          "full paper is anchored to observed opposite edge");
  }
}
void RedPaperWithOneBackgroundRim() {
  constexpr int w=300,h=280;
  std::vector<uint8_t> image(size_t(w)*h*4,255);
  Fill(image,w,{0,0,w,h},41);
  for(int y=20;y<260;++y) for(int x=90;x<260;++x) Pixel(image,w,x,y,0,0,255);
  wb::BackgroundModel bg;bg.center_lab=wb::BgrToLab(45,45,45);bg.weak_delta_e=6;
  auto out=sct::ObserveCanvasExcludingBackground(image.data(),w,h,w*4,{30,20,260,260},-100,50,bg,1,true,170,240);
  Check(!out.ambiguous && out.bounds_capture.left==90 && out.bounds_capture.right==260 &&
        out.bounds_capture.top==20 && out.bounds_capture.bottom==260,
        "solid red artwork remains canvas evidence with a single panel rim");
  Check(out.bounds_screen.left==-10 && out.bounds_screen.top==70,"navigator bounds preserve negative capture origin");
}
}
int main(int argc,char** argv) {
  if(argc==4 || argc==5) {
    const int w=std::atoi(argv[2]),h=std::atoi(argv[3]);
    if(w<300 || h<360 || w>16384 || h>16384) return 2;
    std::vector<uint8_t> image(size_t(w)*h*4);
    std::ifstream file(argv[1],std::ios::binary);
    if(!file.read(reinterpret_cast<char*>(image.data()),image.size())) return 2;
    auto in=argc==5 ? FullyCroppedInput(image,w,h) : WideViewportInput(image,w,h);
    if(argc==5 && std::atoi(argv[4])==1) {
      in.thumbnail_roi={37,101,w,432};in.navigator_canvas_bounds={93,101,326,432};
    }
    const auto out=sct::CompleteViewportFrame(in);
    std::printf("screenshot replay: status=%d %s origin=(%.2f,%.2f) size=%.2fx%.2f\n",
        int(out.status),out.message,out.frame.origin_top_left_displayed.x,
        out.frame.origin_top_left_displayed.y,out.frame.width,out.frame.height);
    return out.status==sct::FailStatus::Ok ? 0 : 1;
  }
  WideViewportWithArtwork();WideViewportWithSeparateRedGroup();SeparateRectanglesStayAmbiguous();
  CompleteFrameOutranksUnanchoredArtwork();ClippedFrameOutranksCornerAndFragmentArtwork();
  SeparateClippedFramesStayAmbiguous();SeparateUnanchoredFragmentsStayAmbiguous();
  PaleSidesRecoverFromAdjacentEdges();CurvedRedStrokesAreRejected();
  StraightFrameSurvivesNearbyRedMark();
  CrossingStrokesAreNotCorners();PaperAspectDoesNotFilterViewport();
  SingleWrongAspectIsRejected();DiagonalDirectionFamilies();
  ClippedPaperModelKeepsExtent();RedPaperWithOneBackgroundRim();
  std::printf("Viewport robustness failures: %d\n",failures);
  return failures ? 1 : 0;
}
