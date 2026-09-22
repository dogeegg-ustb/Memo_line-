#include "sct/canvas_observe.hpp"
#include "wb/color.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <limits>
#include <vector>

namespace sct {

CanvasObservation ObserveCanvasExcludingBackground(
    const uint8_t* bgra, int width, int height, int stride, const wb::IntRect& roi_capture,
    int origin_x, int origin_y, const wb::BackgroundModel& model, float dpi_scale, bool navigator) {
  CanvasObservation out;
  auto fail = [&](const char* reason) {
    out.ambiguous = true;
    std::snprintf(out.ambiguity_reason, sizeof(out.ambiguity_reason), "%s", reason);
    return out;
  };
  if (!bgra || width <= 0 || height <= 0 || width > std::numeric_limits<int>::max()/4 ||
      stride < width*4 || !roi_capture.valid()) return fail("invalid input");
  if (!std::isfinite(model.weak_delta_e) || model.weak_delta_e <= 0 ||
      !std::isfinite(model.center_lab.L) || !std::isfinite(model.center_lab.a) ||
      !std::isfinite(model.center_lab.b)) return fail("invalid background model");
  const auto roi = roi_capture.Clamp(width, height);
  if (!roi.valid()) return fail("roi empty");
  const int rw=roi.width(), rh=roi.height();
  const size_t count=size_t(rw)*rh;
  std::vector<uint8_t> background(count,0), exterior(count,0);
  auto index=[&](int x,int y) { return size_t(y)*rw+x; };
  for (int y=0;y<rh;++y) for (int x=0;x<rw;++x) {
    const auto* p=bgra+size_t(y+roi.top)*stride+size_t(x+roi.left)*4;
    background[index(x,y)]=wb::DeltaE76(wb::BgrToLab(p[0],p[1],p[2]),model.center_lab)
                              <=model.weak_delta_e;
  }

  // Remove only background connected to the ROI exterior. A painted patch
  // matching the background inside the canvas is not exterior background.
  // Seed every exterior component, including L/U shapes and opposite bands.
  std::vector<size_t> pending;
  auto seed=[&](int x,int y) {
    const size_t i=index(x,y);
    if (background[i] && !exterior[i]) {exterior[i]=1;pending.push_back(i);}
  };
  for (int x=0;x<rw;++x) {seed(x,0);seed(x,rh-1);}
  for (int y=0;y<rh;++y) {seed(0,y);seed(rw-1,y);}
  for (size_t head=0;head<pending.size();++head) {
    const size_t i=pending[head];const int x=int(i%rw),y=int(i/rw);
    if(x>0)seed(x-1,y);if(x+1<rw)seed(x+1,y);
    if(y>0)seed(x,y-1);if(y+1<rh)seed(x,y+1);
  }
  // The workspace can contain small non-background UI remnants near its rim.
  // They must not be merged into the displayed canvas. Select one connected
  // foreground body; a canvas with artwork remains connected to its paper.
  std::vector<uint8_t> visited(count,0);
  int minx=rw,miny=rh,maxx=-1,maxy=-1,best_area=0;
  for(int sy=0;sy<rh;++sy) for(int sx=0;sx<rw;++sx) {
    const size_t initial=index(sx,sy);
    if(exterior[initial] || visited[initial]) continue;
    std::vector<size_t> component{initial}; visited[initial]=1;
    int cx0=sx,cx1=sx,cy0=sy,cy1=sy;
    for(size_t head=0;head<component.size();++head) {
      const size_t i=component[head]; const int x=int(i%rw),y=int(i/rw);
      cx0=std::min(cx0,x);cx1=std::max(cx1,x);cy0=std::min(cy0,y);cy1=std::max(cy1,y);
      auto add=[&](int nx,int ny) {
        const size_t ni=index(nx,ny);
        if(!exterior[ni]&&!visited[ni]) {visited[ni]=1;component.push_back(ni);}
      };
      if(x>0)add(x-1,y);if(x+1<rw)add(x+1,y);if(y>0)add(x,y-1);if(y+1<rh)add(x,y+1);
    }
    if(static_cast<int>(component.size())>best_area) {
      best_area=static_cast<int>(component.size());minx=cx0;maxx=cx1;miny=cy0;maxy=cy1;
    }
  }
  if(maxx<minx || maxy<miny) return fail("no separable foreground canvas");
  if (navigator) {
    // Connectivity is useful for keeping artwork inside the paper, but a red
    // viewport can also enclose exterior gray. Such a pocket is NOT paper
    // evidence. Measure the bounds from actual non-background pixels, ignoring
    // red viewport ink (including AA) so long vertical strokes cannot support
    // a false left/right boundary. This evidence mask is only used here; the
    // workspace observation and the original image/viewport detection stay intact.
    std::vector<int> columns(rw,0), rows(rh,0);
    for(int y=miny;y<=maxy;++y) for(int x=minx;x<=maxx;++x) {
      const auto* p=bgra+size_t(y+roi.top)*stride+size_t(x+roi.left)*4;
      const int b=p[0], g=p[1], r=p[2];
      const bool viewport_ink = r >= 70 && r-g >= 8 && r-b >= 8;
      if(!background[index(x,y)] && !viewport_ink) {++columns[x];++rows[y];}
    }
    const int minColumn = std::max(2,*std::max_element(columns.begin(),columns.end())/10);
    const int minRow = std::max(2,*std::max_element(rows.begin(),rows.end())/10);
    while(minx<=maxx && columns[minx]<minColumn) ++minx;
    while(maxx>=minx && columns[maxx]<minColumn) --maxx;
    while(miny<=maxy && rows[miny]<minRow) ++miny;
    while(maxy>=miny && rows[maxy]<minRow) --maxy;
    if(maxx<minx || maxy<miny) return fail("no supported navigator canvas body");
  }
  out.bounds_capture={roi.left+minx,roi.top+miny,roi.left+maxx+1,roi.top+maxy+1};
  out.bounds_screen={out.bounds_capture.left+origin_x,out.bounds_capture.top+origin_y,
                     out.bounds_capture.right+origin_x,out.bounds_capture.bottom+origin_y};
  const int bw=maxx-minx+1,bh=maxy-miny+1;
  out.aspect_ratio=float(bw)/bh;

  // AABB line support is a separate diagnostic for rectangular direct mapping.
  // It does not decide whether an arbitrary foreground is surrounded.
  for(int side=0;side<4;++side) {
    int hits=0,total=0;
    if(side==0 || side==2) {
      int x=side==0?minx:maxx;
      for(int y=miny;y<=maxy;++y) {++total;hits+=!exterior[index(x,y)];}
    } else {
      int y=side==1?miny:maxy;
      for(int x=minx;x<=maxx;++x) {++total;hits+=!exterior[index(x,y)];}
    }
    out.boundary_support[side]=float(hits)/total;
    if(out.boundary_support[side]>=0.55f) out.visible_edges_mask|=1<<side;
  }
  const int depth=std::clamp(int(std::lround(2*(std::isfinite(dpi_scale)?dpi_scale:1.f))),1,4);
  const int gaps[4]={minx,miny,rw-1-maxx,rh-1-maxy};
  int surrounded_sides=0;
  for(int side=0;side<4;++side) {
    // A thin visible rim is valid. Never demand a band proportional to canvas
    // area or clamp a missing exterior sample to an inside pixel.
    const int d=std::min(depth,gaps[side]);
    if(d<1)continue;
    int hits=0,total=0;
    for(int k=1;k<=d;++k) {
      if(side==0 || side==2) {
        const int x=side==0?minx-k:maxx+k;
        for(int y=miny;y<=maxy;++y) {++total;hits+=exterior[index(x,y)];}
      } else {
        const int y=side==1?miny-k:maxy+k;
        for(int x=minx;x<=maxx;++x) {++total;hits+=exterior[index(x,y)];}
      }
    }
    if(total>0 && float(hits)/total>=0.95f)++surrounded_sides;
  }
  out.four_sides_complete=surrounded_sides==4;
  out.confidence=0.5f+0.125f*surrounded_sides;
  return out;
}
}  // namespace sct
