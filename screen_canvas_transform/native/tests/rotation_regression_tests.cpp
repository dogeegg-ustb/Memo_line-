#include "sct/viewport_frame.hpp"
#include "sct/transform_solve.hpp"
#include <cmath>
#include <cstdio>
#include <limits>
#include <vector>

namespace {
int failures = 0;
void Check(bool ok, const char* message, double angle) {
  if (!ok) { ++failures; std::printf("FAIL angle=%.1f %s\n", angle, message); }
}
void Line(std::vector<uint8_t>& image, int w, int h, sct::Vec2 a, sct::Vec2 b) {
  int n = int(std::ceil(std::hypot(b.x-a.x,b.y-a.y)*2));
  for (int i=0;i<=n;++i) {
    double t=double(i)/n;
    int x=int(std::lround(a.x+(b.x-a.x)*t)), y=int(std::lround(a.y+(b.y-a.y)*t));
    if (x>=0 && x<w && y>=0 && y<h) {
      auto* p=&image[(y*w+x)*4]; p[0]=0;p[1]=0;p[2]=220;p[3]=255;
    }
  }
}
void Test(double angle, bool clipped) {
  constexpr int W=320,H=300;
  std::vector<uint8_t> image(W*H*4,255);
  const double a=-angle*0.017453292519943295, c=std::cos(a),s=std::sin(a);
  const double width=120,height=80;
  auto point=[&](double x,double y) { return sct::Vec2{160+c*x-s*y,150+s*x+c*y}; };
  sct::Vec2 corners[]={point(-60,-40),point(60,-40),point(60,40),point(-60,40)};
  for(int i=0;i<4;++i) if(!clipped || i!=2) Line(image,W,H,corners[i],corners[(i+1)%4]);
  sct::ViewportCompletionInput in;
  in.bgra=image.data();in.width=W;in.height=H;in.stride=W*4;
  in.thumbnail_roi={0,0,W,H};in.navigator_canvas_bounds={10,10,310,290};
  in.display_rotation_degrees=float(angle);in.display_rotation_confidence=1;
  in.workspace_canvas_relation.workspace_roi={-1700,100,-800,700};
  // Deliberately unrelated canvas aspect: viewport aspect belongs to workspace.
  in.workspace_canvas_relation.canvas_aspect_ratio=2.0f;
  auto out=sct::CompleteViewportFrame(in);
  Check(out.status==sct::FailStatus::Ok,clipped?"U completion":"rectangle completion",angle);
  if(out.status!=sct::FailStatus::Ok) {std::printf("  %s\n",out.message);return;}
  Check(std::hypot(out.frame.origin_top_left_displayed.x-corners[0].x-0.5,
                   out.frame.origin_top_left_displayed.y-corners[0].y-0.5)<2,
        "directed origin",angle);
  Check(std::hypot(out.frame.axis_x_displayed.x-c*width,out.frame.axis_x_displayed.y-s*width)<2,
        "directed X",angle);
  Check(std::hypot(out.frame.axis_y_displayed.x+s*height,out.frame.axis_y_displayed.y-c*height)<2,
        "directed Y",angle);
  sct::SolveInput solve;
  std::snprintf(solve.capture_id,sizeof(solve.capture_id),"raster-to-screen");
  solve.canvas_pixel_width=3000;solve.canvas_pixel_height=2800;
  solve.injected_scale_percent=50;
  solve.workspace_roi_screen=in.workspace_canvas_relation.workspace_roi;
  solve.navigator_canvas.bounds_capture=in.navigator_canvas_bounds;
  solve.viewport=out.frame;
  auto result=sct::SolveTransform(solve);
  Check(result.status==sct::FailStatus::Ok,"end-to-end solve",angle);
  for (double u:{0.,0.5,1.}) for (double v:{0.,0.5,1.}) {
    const double dx=10+300*u-corners[0].x-0.5;
    const double dy=10+280*v-corners[0].y-0.5;
    sct::Vec2 want{-1700+(c*dx+s*dy)*900/width,
                    100+(-s*dx+c*dy)*600/height};
    auto got=result.snapshot.canvas_to_screen.Apply({u,v});
    // Rasterization uncertainty is magnified by the thumbnail-to-screen scale.
    Check(std::hypot(want.x-got.x,want.y-got.y)<20,"raster absolute position",angle);
  }
}
void TwoSideObliqueTest(double angle) {
  constexpr int W=320,H=300;
  std::vector<uint8_t> image(W*H*4,255);
  const double a=-angle*0.017453292519943295,c=std::cos(a),s=std::sin(a);
  auto point=[&](double x,double y) { return sct::Vec2{160+c*x-s*y,150+s*x+c*y}; };
  sct::Vec2 corners[]={point(-60,-40),point(60,-40),point(60,40),point(-60,40)};
  Line(image,W,H,corners[0],corners[1]);
  Line(image,W,H,corners[0],corners[3]);
  sct::ViewportCompletionInput in;
  in.bgra=image.data();in.width=W;in.height=H;in.stride=W*4;
  in.thumbnail_roi={0,0,W,H};in.navigator_canvas_bounds={10,10,310,290};
  in.display_rotation_degrees=float(angle);in.display_rotation_confidence=1;
  in.workspace_canvas_relation.workspace_roi={0,0,120,80};
  auto out=sct::CompleteViewportFrame(in);
  Check(out.status==sct::FailStatus::Ok,"two-side oblique completion",angle);
  if(out.status!=sct::FailStatus::Ok) {std::printf("  %s\n",out.message);return;}
  Check(std::hypot(out.frame.origin_top_left_displayed.x-corners[0].x-0.5,
                   out.frame.origin_top_left_displayed.y-corners[0].y-0.5)<2,
        "two-side directed origin",angle);
  Check(std::abs(out.frame.width-120)<=4 && std::abs(out.frame.height-80)<=4,
        "two-side directed size",angle);
}
void MatrixTest(double angle) {
  // Independent physical ground truth: canvas (u,v) -> rotated screen pixels.
  const double a=angle*0.017453292519943295,c=std::cos(a),s=std::sin(a);
  auto screen=[&](double u,double v) {return sct::Vec2{-1350+c*1200*u-s*600*v,
                                                       250+s*1200*u+c*600*v};};
  sct::SolveInput in;
  std::snprintf(in.capture_id,sizeof(in.capture_id),"physical-fixture");
  in.canvas_pixel_width=2400;in.canvas_pixel_height=1200;in.injected_scale_percent=50;
  in.workspace_roi_screen={-1700,100,-800,700};
  in.navigator_canvas.bounds_capture={2200,50,2440,170};
  in.navigator_canvas.bounds_screen={280,50,520,170}; // capture origin = -1920
  auto nav=[&](double x,double y) {
    const double dx=x+1350,dy=y-250;
    return sct::Vec2{2200+(c*dx+s*dy)*0.2,50+(-s*dx+c*dy)*0.2};
  };
  auto o=nav(-1700,100),x=nav(-800,100),y=nav(-1700,700);
  in.viewport.origin_top_left_displayed=o;
  in.viewport.axis_x_displayed={x.x-o.x,x.y-o.y};
  in.viewport.axis_y_displayed={y.x-o.x,y.y-o.y};
  in.viewport.width=180;in.viewport.height=120;
  auto result=sct::SolveTransform(in);
  Check(result.status==sct::FailStatus::Ok,"matrix solve",angle);
  for(double u:{0.0,0.3,1.0}) for(double v:{0.0,0.7,1.0}) {
    auto want=screen(u,v),got=result.snapshot.canvas_to_screen.Apply({u,v});
    Check(std::hypot(want.x-got.x,want.y-got.y)<1e-8,"absolute screen ground truth",angle);
    auto uv=result.snapshot.screen_to_canvas.Apply(want);
    Check(std::hypot(uv.x-u,uv.y-v)<1e-10,"screen inverse ground truth",angle);
  }
  in.viewport.origin_top_left_displayed.x=std::numeric_limits<double>::quiet_NaN();
  Check(sct::SolveTransform(in).status!=sct::FailStatus::Ok,"reject NaN",angle);
}
void DirectPathRotationTest() {
  sct::SolveInput in;
  std::snprintf(in.capture_id,sizeof(in.capture_id),"rotated-aabb");
  in.canvas_pixel_width=1000;in.canvas_pixel_height=500;
  in.injected_scale_percent=50;
  in.workspace_roi_screen={0,0,800,600};
  in.workspace_canvas.bounds_screen={100,100,600,350};
  in.workspace_canvas.four_sides_complete=true;
  for (float& support : in.workspace_canvas.boundary_support) support=1;
  in.numbers.rotation_confidence=1;
  in.numbers.rotation_degrees=0;
  auto direct=sct::SolveTransform(in);
  Check(direct.status==sct::FailStatus::Ok && direct.snapshot.used_direct_workspace_path,
        "matching archived aspect permits direct matrix",0);
  in.workspace_canvas.bounds_screen={100,100,550,350}; // 1.8, but archive is 2.0
  Check(sct::SolveTransform(in).status!=sct::FailStatus::Ok,
        "aspect-mismatched rectangle cannot become direct matrix",0);
  in.workspace_canvas.bounds_screen={100,100,600,350};
  for (float angle:{90.f,180.f,25.f}) {
    in.numbers.rotation_degrees=angle;
    Check(sct::SolveTransform(in).status!=sct::FailStatus::Ok,
          "rotated AABB cannot silently become direct matrix",angle);
  }
  // An axis-aligned bounding box cannot distinguish 0° from 180°.  An unread
  // rotation must not silently become the direct 0° mapping.
  in.numbers.rotation_confidence=0;
  in.numbers.rotation_degrees=90;
  Check(sct::SolveTransform(in).status!=sct::FailStatus::Ok,
        "unread rotation cannot become a direct 0-degree matrix",0);
}
void SingleClippedEdgeTest() {
  // Canvas top-left is deliberately far from the visible fragment's midpoint.
  for (int left : {80,210}) {
    constexpr int W=320,H=300;
    std::vector<uint8_t> image(W*H*4,255);
    Line(image,W,H,{0,220},{319,220}); // includes navigator background margins
    sct::ViewportCompletionInput in;
    in.bgra=image.data();in.width=W;in.height=H;in.stride=W*4;
    in.thumbnail_roi={0,0,W,H};in.navigator_canvas_bounds={80,20,240,260};
    in.display_rotation_confidence=1;
    auto& rel=in.workspace_canvas_relation;
    rel.workspace_roi={-1200,100,-400,700};
    rel.visible_canvas_bounds_workspace_local={left,100,left+400,600};
    rel.canvas_crop_sides=8;rel.confidence=1;
    auto out=sct::CompleteViewportFrame(in);
    Check(out.status==sct::FailStatus::Ok,"single clipped edge completion",0);
    if(out.status!=sct::FailStatus::Ok) continue;
    // Only 160 px of the red edge pass through the Navigator canvas. Recover
    // the complete viewport side by dividing by the corresponding workspace
    // overlap: 160 / (400 / 800) = 320; height = 320 * (600 / 800) = 240.
    Check(std::abs(out.frame.width-320)<2 && std::abs(out.frame.height-240)<2,
          "single edge divides red-on-canvas pixels by workspace overlap",0);
  }
  auto m=sct::Affine2D::FromCorners({2,3},{4,3},{2,7},{10,20},{14,22},{6,28});
  auto p=m.Apply({4,7});
  Check(std::hypot(p.x-10,p.y-30)<1e-9,"FromCorners actual affine map",0);
  auto bad=sct::Affine2D::FromCorners({0,0},{1,0},{2,0},{0,0},{1,0},{0,1});
  Check(!std::isfinite(bad.m[0]),"FromCorners rejects collinear inputs",0);
}

void OneCompleteEdgeUsesAnchoredWorkspaceCanvasRelation() {
  // The paper is fully visible across X but starts well inside the workspace
  // and is clipped at its bottom. The Navigator's complete horizontal red side
  // is therefore clipped to the paper width. Mapping the complete workspace to
  // that segment moves canvas (0,0) to the workspace's upper-left instead of
  // the observed paper corner.
  sct::SolveInput in;
  std::snprintf(in.capture_id, sizeof(in.capture_id), "one-complete-offset");
  in.canvas_pixel_width = 4961;
  in.canvas_pixel_height = 7016;
  in.injected_scale_percent = 30;
  in.workspace_roi_screen = {409,149,2174,1431};
  in.navigator_canvas.bounds_capture = {2244,143,2521,473};
  in.viewport.origin_top_left_displayed = {2244.5,64.0};
  in.viewport.axis_x_displayed = {275.9,0.0};
  in.viewport.axis_y_displayed = {0.0,200.4};
  in.viewport.width = 275.9f;
  in.viewport.height = 200.4f;
  in.viewport.completion_strategy =
      static_cast<int>(sct::ViewportCompletionPattern::OneCompleteEdge);

  auto& rel = in.workspace_canvas_relation;
  rel.workspace_roi = in.workspace_roi_screen;
  rel.visible_canvas_bounds_workspace_local = {173,509,1661,1282};
  rel.full_canvas_model_workspace_local = {173,509,1661,2613};
  rel.canvas_crop_sides = 8;  // bottom only
  rel.canvas_aspect_ratio = static_cast<float>(4961.0 / 7016.0);
  rel.confidence = 0.9f;

  const auto result = sct::SolveTransform(in);
  Check(result.status == sct::FailStatus::Ok,
        "one complete edge offset relation solves", 0);
  if (result.status != sct::FailStatus::Ok) return;

  const auto paper_tl = result.snapshot.canvas_to_screen.Apply({0,0});
  const auto paper_br = result.snapshot.canvas_to_screen.Apply({1,1});
  Check(std::hypot(paper_tl.x - 582.0, paper_tl.y - 658.0) < 1e-6,
        "one complete edge preserves visible paper top-left translation", 0);
  Check(std::hypot(paper_br.x - 2070.0, paper_br.y - 2762.0) < 1e-6,
        "one complete edge keeps archived full-paper scale", 0);
  const auto canvas_tl = result.snapshot.screen_to_canvas.Apply({582,658});
  Check(std::hypot(canvas_tl.x, canvas_tl.y) < 1e-9,
        "observed paper top-left maps to canvas origin", 0);
}
}
int main() {
  SingleClippedEdgeTest();
  OneCompleteEdgeUsesAnchoredWorkspaceCanvasRelation();
  DirectPathRotationTest();
  for(double a:{0.,5.,25.,-30.,45.,60.,89.,90.,135.,180.,-135.,-90.}) {
    Test(a,false);Test(a,true);MatrixTest(a);
  }
  for(double a:{5.,25.,-30.,45.,60.,135.,-135.}) TwoSideObliqueTest(a);
  std::printf("Rotation regression failures: %d\n",failures);
  return failures?1:0;
}
