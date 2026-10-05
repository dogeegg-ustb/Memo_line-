#include "sct/canvas_observe.hpp"
#include "wb/color.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdio>
#include <limits>
#include <unordered_map>
#include <utility>
#include <vector>

namespace sct {
namespace {

struct BgrColor {
  int b = 0;
  int g = 0;
  int r = 0;
};

// The workspace detector owns the background model in Lab. Convert its centre
// back to the captured 8-bit colour so canvas observation can use a deliberately
// narrow, non-drifting colour range. Windows screen capture preserves flat UI
// fills exactly; the small weak range only absorbs conversion round-off.
BgrColor LabCenterToBgr(const wb::Lab& lab) {
  auto inverse_f = [](double t) {
    constexpr double d = 6.0 / 29.0;
    return t > d ? t * t * t : 3.0 * d * d * (t - 4.0 / 29.0);
  };
  const double fy = (lab.L + 16.0) / 116.0;
  const double fx = fy + lab.a / 500.0;
  const double fz = fy - lab.b / 200.0;
  const double x = 0.95047 * inverse_f(fx);
  const double y = inverse_f(fy);
  const double z = 1.08883 * inverse_f(fz);
  double r =  3.2404542 * x - 1.5371385 * y - 0.4985314 * z;
  double g = -0.9692660 * x + 1.8760108 * y + 0.0415560 * z;
  double b =  0.0556434 * x - 0.2040259 * y + 1.0572252 * z;
  auto encode = [](double c) {
    c = c <= 0.0031308 ? 12.92 * c : 1.055 * std::pow(c, 1.0 / 2.4) - 0.055;
    return std::clamp(static_cast<int>(std::lround(c * 255.0)), 0, 255);
  };
  return {encode(b), encode(g), encode(r)};
}

int BgrDistance(const uint8_t* p, const BgrColor& c) {
  return std::max({std::abs(int(p[0]) - c.b), std::abs(int(p[1]) - c.g),
                   std::abs(int(p[2]) - c.r)});
}

BgrColor NavigatorBackgroundFromRim(const uint8_t* bgra, int stride,
                                    const wb::IntRect& roi, const BgrColor& workspace_bg) {
  struct Candidate {
    int pixels = 0;
  };
  std::unordered_map<uint32_t, Candidate> candidates;
  const int band = std::min({8, roi.width() / 4, roi.height() / 4});
  if (band < 1) return workspace_bg;
  for (int y = roi.top; y < roi.bottom; ++y) {
    for (int x = roi.left; x < roi.right; ++x) {
      if (x - roi.left >= band && roi.right - 1 - x >= band &&
          y - roi.top >= band && roi.bottom - 1 - y >= band) continue;
      const auto* p = bgra + size_t(y) * stride + size_t(x) * 4;
      // Only nearby UI fills can replace the workspace background. Artwork
      // and white paper on a tightly cropped rim must not become background.
      if (BgrDistance(p, workspace_bg) > 24) continue;
      const uint32_t key = uint32_t(p[0]) | (uint32_t(p[1]) << 8) |
                           (uint32_t(p[2]) << 16);
      auto& candidate = candidates[key];
      ++candidate.pixels;
    }
  }
  uint32_t best_key = 0;
  int best_count = 0;
  for (const auto& [key, candidate] : candidates) {
    // C-II may crop the thumbnail to the image's right/top/bottom edge. The
    // surrounding panel is then exposed on only one side of this ROI.
    if (candidate.pixels <= best_count) continue;
    best_key = key;
    best_count = candidate.pixels;
  }
  if (best_count < std::max(16, band * 4)) return workspace_bg;
  return {int(best_key & 255), int((best_key >> 8) & 255),
          int((best_key >> 16) & 255)};
}

}  // namespace

CanvasObservation ObserveCanvasExcludingBackground(
    const uint8_t* bgra, int width, int height, int stride, const wb::IntRect& roi_capture,
    int origin_x, int origin_y, const wb::BackgroundModel& model, float dpi_scale,
    bool navigator, int canvas_pixel_width, int canvas_pixel_height) {
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
  std::vector<uint8_t> strong_background(count,0), background(count,0), exterior(count,0);
  auto index=[&](int x,int y) { return size_t(y)*rw+x; };
  // Keep the narrow range for both observations, but center the navigator mask
  // on its own panel fill. CSP can draw that panel a few RGB levels away from
  // the workspace; with a fixed workspace center the entire panel becomes
  // foreground and pulls the measured canvas boundary to the ROI edge.
  const BgrColor workspace_bg = LabCenterToBgr(model.center_lab);
  const BgrColor model_bg = navigator
      ? NavigatorBackgroundFromRim(bgra, stride, roi, workspace_bg)
      : workspace_bg;
  std::array<uint32_t,4096> color_keys;
  color_keys.fill(0xffffffffu);
  std::array<uint8_t,4096> color_values{};
  std::array<uint8_t,4096> strong_values{};
  for (int y=0;y<rh;++y) for (int x=0;x<rw;++x) {
    const auto* p=bgra+size_t(y+roi.top)*stride+size_t(x+roi.left)*4;
    const uint32_t key=uint32_t(p[0]) | (uint32_t(p[1])<<8) | (uint32_t(p[2])<<16);
    const size_t slot=(key*2654435761u)>>20;
    if(color_keys[slot]!=key) {
      const int distance=BgrDistance(p,model_bg);
      strong_values[slot]=distance<=1;
      color_values[slot]=distance<=3;
      color_keys[slot]=key;
    }
    background[index(x,y)]=color_values[slot];
    strong_background[index(x,y)]=strong_values[slot];
  }

  // Grow only from strict background pixels on the ROI rim, through the weak
  // narrow mask. Four-connectivity and a fixed model prevent colour drift.
  std::vector<size_t> pending;
  pending.reserve(count);
  auto seed=[&](int x,int y) {
    const size_t i=index(x,y);
    if (strong_background[i] && !exterior[i]) {exterior[i]=1;pending.push_back(i);}
  };
  for (int x=0;x<rw;++x) {seed(x,0);seed(x,rh-1);}
  for (int y=0;y<rh;++y) {seed(0,y);seed(rw-1,y);}
  if (!navigator) {
    // A thin UI outline on the stored ROI rim must not seal off the actual
    // workspace fill. The color gate stays strict; only the seed location
    // tolerates a three-pixel border remnant.
    const int rim = std::min({3, rw / 2, rh / 2});
    for (int k=1;k<=rim;++k) {
      for (int x=0;x<rw;++x) {seed(x,k);seed(x,rh-1-k);}
      for (int y=0;y<rh;++y) {seed(k,y);seed(rw-1-k,y);}
    }
  }
  if (pending.empty()) return fail("no strict UI background on roi boundary");
  for (size_t head=0;head<pending.size();++head) {
    const size_t i=pending[head];const int x=int(i%rw),y=int(i/rw);
    auto grow=[&](int nx,int ny) {
      const size_t ni=index(nx,ny);
      if(background[ni]&&!exterior[ni]) {exterior[ni]=1;pending.push_back(ni);}
    };
    if(x>0)grow(x-1,y);if(x+1<rw)grow(x+1,y);
    if(y>0)grow(x,y-1);if(y+1<rh)grow(x,y+1);
  }
  // The workspace can contain small non-background UI remnants near its rim.
  // They must not be merged into the displayed canvas. Select one connected
  // foreground body; a canvas with artwork remains connected to its paper.
  std::vector<uint8_t> visited(count,0), canvas_component(count,0);
  int minx=rw,miny=rh,maxx=-1,maxy=-1,best_area=0;
  for(int sy=0;sy<rh;++sy) for(int sx=0;sx<rw;++sx) {
    const size_t initial=index(sx,sy);
    if(exterior[initial] || visited[initial]) continue;
    // The exterior flood is finished. Reuse its queue for each component.
    auto& component=pending;
    component.clear(); component.push_back(initial); visited[initial]=1;
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
      std::fill(canvas_component.begin(),canvas_component.end(),0);
      for(const size_t i:component) canvas_component[i]=1;
    }
  }
  if(maxx<minx || maxy<miny) return fail("no separable foreground canvas");
  const int touching_sides = (minx <= 3) + (miny <= 3) +
      (rw - 1 - maxx <= 3) + (rh - 1 - maxy <= 3);
  if (!navigator && touching_sides >= 3) {
    // A canvas touching a UI remnant can share its connected component with
    // the thin workspace outline. An AABB then becomes the whole workspace,
    // incorrectly declaring all four paper sides cropped. Measure the body
    // from sustained foreground density, excluding trapped background pixels.
    std::vector<int> columns(rw,0), rows(rh,0);
    for(int y=miny;y<=maxy;++y) for(int x=minx;x<=maxx;++x)
      if(canvas_component[index(x,y)] && !background[index(x,y)]) ++columns[x];
    auto body_interval=[](const std::vector<int>& density,int& first,int& last) {
      const int threshold=std::max(2,*std::max_element(density.begin(),density.end())/10);
      int best_start=-1,best_length=0;
      for(int i=0;i<int(density.size());) {
        if(density[i]<threshold) {++i;continue;}
        const int start=i;
        while(i<int(density.size()) && density[i]>=threshold) ++i;
        if(i-start>best_length) {best_start=start;best_length=i-start;}
      }
      if(best_length<2) return false;
      first=best_start;last=best_start+best_length-1;
      return true;
    };
    if(!body_interval(columns,minx,maxx)) return fail("no supported workspace canvas columns");
    for(int y=miny;y<=maxy;++y) for(int x=minx;x<=maxx;++x)
      if(canvas_component[index(x,y)] && !background[index(x,y)]) ++rows[y];
    if(!body_interval(rows,miny,maxy)) return fail("no supported workspace canvas rows");
  }
  int clip_ext[4]={0,0,0,0};  // navigator paper continuing past the ROI (L/T/R/B px)
  if (navigator) {
    // Connectivity is useful for keeping artwork inside the paper, but a red
    // viewport can also enclose exterior gray. Such a pocket is NOT paper
    // evidence. Measure the bounds from actual non-background pixels, ignoring
    // red viewport ink (including AA) so long vertical strokes cannot support
    // a false left/right boundary. This evidence mask is only used here; the
    // workspace observation and the original image/viewport detection stay intact.
    std::vector<uint8_t> red_core(count,0), thin_core(count,0), viewport_ink(count,0);
    for(int y=0;y<rh;++y) for(int x=0;x<rw;++x) {
      const auto* p=bgra+size_t(y+roi.top)*stride+size_t(x+roi.left)*4;
      red_core[index(x,y)]=p[2]>=100 && int(p[2])-std::max(p[0],p[1])>=30;
    }
    auto core_at=[&](int x,int y) {
      return x>=0 && y>=0 && x<rw && y<rh && red_core[index(x,y)];
    };
    for(int y=0;y<rh;++y) for(int x=0;x<rw;++x) {
      if (!red_core[index(x,y)]) continue;
      for(int direction=0;direction<8;++direction) {
        const double angle=direction*3.14159265358979323846/8;
        const int nx=int(std::lround(5*std::cos(angle))),ny=int(std::lround(5*std::sin(angle)));
        if (core_at(x+nx,y+ny) || core_at(x-nx,y-ny)) continue;
        const int tx=int(std::lround(-6*std::sin(angle))),ty=int(std::lround(6*std::cos(angle)));
        auto nearby_core=[&](int px,int py) {
          for(int dy=-1;dy<=1;++dy) for(int dx=-1;dx<=1;++dx)
            if(core_at(px+dx,py+dy)) return true;
          return false;
        };
        if (nearby_core(x+tx,y+ty) && nearby_core(x-tx,y-ty)) {
          thin_core[index(x,y)]=1; break;
        }
      }
    }
    // Suppress only thin straight red strokes and their AA fringe. Broad red
    // artwork remains paper evidence; red dominance alone is not viewport ink.
    for(int y=0;y<rh;++y) for(int x=0;x<rw;++x) {
      const auto* p=bgra+size_t(y+roi.top)*stride+size_t(x+roi.left)*4;
      if (!(p[2]>=70 && int(p[2])-p[1]>=8 && int(p[2])-p[0]>=8)) continue;
      for(int dy=-2;dy<=2 && !viewport_ink[index(x,y)];++dy)
        for(int dx=-2;dx<=2;++dx)
          if(x+dx>=0 && y+dy>=0 && x+dx<rw && y+dy<rh && thin_core[index(x+dx,y+dy)]) {
            viewport_ink[index(x,y)]=1;break;
          }
    }
    std::vector<int> columns(rw,0), rows(rh,0);
    for(int y=miny;y<=maxy;++y) for(int x=minx;x<=maxx;++x) {
      if(!background[index(x,y)] && !viewport_ink[index(x,y)]) {++columns[x];++rows[y];}
    }
    const int minColumn = std::max(2,*std::max_element(columns.begin(),columns.end())/10);
    const int minRow = std::max(2,*std::max_element(rows.begin(),rows.end())/10);
    while(minx<=maxx && columns[minx]<minColumn) ++minx;
    while(maxx>=minx && columns[maxx]<minColumn) --maxx;
    while(miny<=maxy && rows[miny]<minRow) ++miny;
    while(maxy>=miny && rows[maxy]<minRow) --maxy;
    if(maxx<minx || maxy<miny) return fail("no supported navigator canvas body");

    // CSP frames the Navigator view with lines a few levels off its fill. A
    // line touching the paper joins the paper's component (often through red
    // viewport ink) and survives the density trim, shifting a paper bound by
    // its width. A frame line runs on past the paper's perpendicular extent;
    // paper itself is background there. Conversely a frozen thumbnail ROI a
    // few px inside the view clips a genuine paper edge: continue that edge
    // into the real frame pixels while the column/row is still paper.
    auto paper_at=[&](int x,int y) {  // ROI-local coordinates, may leave the ROI
      const int ax=x+roi.left, ay=y+roi.top;
      if(ax<0 || ay<0 || ax>=width || ay>=height) return -1;
      if(x>=0 && y>=0 && x<rw && y<rh) return viewport_ink[index(x,y)] ? -1 : int(!background[index(x,y)]);
      const auto* p=bgra+size_t(ay)*stride+size_t(ax)*4;
      if(p[2]>=70 && int(p[2])-p[1]>=8 && int(p[2])-p[0]>=8) return -1;  // red viewport ink
      return int(BgrDistance(p,model_bg)>3);
    };
    // Share of non-background pixels of one line, inside / outside the paper span.
    auto line_share=[&](bool column,int at,bool inside) {
      int hits=0,total=0;
      const int lo=column?miny:minx, hi=column?maxy:maxx, n=column?rh:rw;
      for(int k=0;k<n;++k) {
        if((k>=lo && k<=hi)!=inside) continue;
        const int v=column?paper_at(at,k):paper_at(k,at);
        if(v<0) continue;
        ++total; hits+=v;
      }
      return std::make_pair(hits,total);
    };
    constexpr int kFrameLineMaxPx=4, kClippedEdgeMaxPx=6, kOutsideSamplesMin=4;
    auto frame_line=[&](bool column,int at) {
      const auto [hits,total]=line_share(column,at,false);
      if (total<kOutsideSamplesMin || hits<0.8*total) return false;
      // A genuine paper edge can share its column/row with a slightly
      // different gray panel rim. Exterior non-background alone does not
      // make that white paper a frame line: the line's color must continue
      // through both its exterior and its interior span.
      const int lo=column?miny:minx, hi=column?maxy:maxx, n=column?rh:rw;
      std::unordered_map<uint32_t,int> colors;
      uint32_t dominant=0; int best=0;
      for(int k=0;k<n;++k) {
        if(k>=lo && k<=hi) continue;
        const int x=column?at:k, y=column?k:at;
        if(paper_at(x,y)!=1) continue;
        const auto* p=bgra+size_t(y+roi.top)*stride+size_t(x+roi.left)*4;
        const uint32_t color=uint32_t(p[0]) | (uint32_t(p[1])<<8) | (uint32_t(p[2])<<16);
        const int count=++colors[color];
        if(count>best) { best=count; dominant=color; }
      }
      if(best<kOutsideSamplesMin) return false;
      const BgrColor line_color{int(dominant&255),int((dominant>>8)&255),int((dominant>>16)&255)};
      int same=0, inside=0;
      for(int k=lo;k<=hi;++k) {
        const int x=column?at:k, y=column?k:at;
        if(paper_at(x,y)<0) continue;
        const auto* p=bgra+size_t(y+roi.top)*stride+size_t(x+roi.left)*4;
        ++inside; same+=BgrDistance(p,line_color)<=3;
      }
      return inside>0 && same>=0.8*inside;
    };
    auto paper_line=[&](bool column,int at) {
      const auto in=line_share(column,at,true), out=line_share(column,at,false);
      return in.second>0 && in.first>=0.5*in.second &&
             (out.second<kOutsideSamplesMin || out.first<=0.2*out.second);
    };
    // A peeled frame line can expose a sparse line (e.g. one red pixel) that
    // the density trim would have removed had the frame not shielded it.
    for(int pass=0;pass<3;++pass) {
      const int before[4]={minx,miny,maxx,maxy};
      for(int k=0;k<kFrameLineMaxPx && maxx>minx && frame_line(true,minx);++k) ++minx;
      for(int k=0;k<kFrameLineMaxPx && maxx>minx && frame_line(true,maxx);++k) --maxx;
      for(int k=0;k<kFrameLineMaxPx && maxy>miny && frame_line(false,miny);++k) ++miny;
      for(int k=0;k<kFrameLineMaxPx && maxy>miny && frame_line(false,maxy);++k) --maxy;
      while(minx<maxx && columns[minx]<minColumn) ++minx;
      while(maxx>minx && columns[maxx]<minColumn) --maxx;
      while(miny<maxy && rows[miny]<minRow) ++miny;
      while(maxy>miny && rows[maxy]<minRow) --maxy;
      if(before[0]==minx && before[1]==miny && before[2]==maxx && before[3]==maxy) break;
    }
    // Kept apart from the ROI-local bounds, which index ROI-sized masks below.
    if(minx==0) while(clip_ext[0]<kClippedEdgeMaxPx && paper_line(true,-1-clip_ext[0])) ++clip_ext[0];
    if(miny==0) while(clip_ext[1]<kClippedEdgeMaxPx && paper_line(false,-1-clip_ext[1])) ++clip_ext[1];
    if(maxx==rw-1) while(clip_ext[2]<kClippedEdgeMaxPx && paper_line(true,rw+clip_ext[2])) ++clip_ext[2];
    if(maxy==rh-1) while(clip_ext[3]<kClippedEdgeMaxPx && paper_line(false,rh+clip_ext[3])) ++clip_ext[3];
  }
  if (navigator && canvas_pixel_width > 0 && canvas_pixel_height > 0) {
    const double expected_aspect = double(canvas_pixel_width) / canvas_pixel_height;
    const int observed_w = maxx - minx + 1 + clip_ext[0] + clip_ext[2];
    const int observed_h = maxy - miny + 1 + clip_ext[1] + clip_ext[3];
    const double observed_aspect = double(observed_w) / observed_h;
    const double tolerance = std::max(0.04, 2.0 / std::min(observed_w, observed_h));
    if (std::abs(observed_aspect / expected_aspect - 1.0) > tolerance) {
      // Validate both the retained anchor and the inferred opposite edge in
      // the frozen capture. Looking one pixel beyond the ROI is intentional:
      // C-II can crop a genuine image edge exactly at its own boundary.
      auto is_background_at = [&](int x, int y) {
        x += roi.left; y += roi.top;
        if (x < 0 || y < 0 || x >= width || y >= height) return false;
        return BgrDistance(bgra + size_t(y) * stride + size_t(x) * 4, model_bg) <= 3;
      };
      auto edge_score = [&](bool vertical, bool start, int edge, int first, int last) {
        double best = 0.0;
        // Segmentation and line overlays can leave a one or two pixel fringe.
        for (int shift = -3; shift <= 3; ++shift) {
          int hits = 0, total = 0;
          const int candidate_edge = edge + shift;
          for (int along = first + 2; along <= last - 2; ++along) {
            const int outside = candidate_edge + (start ? -1 : 1);
            const bool exterior = vertical ? is_background_at(outside, along)
                                           : is_background_at(along, outside);
            bool interior = false;
            for (int depth = 0; depth < 3; ++depth) {
              const int inside = candidate_edge + (start ? depth : -depth);
              interior |= vertical ? !is_background_at(inside, along)
                                   : !is_background_at(along, inside);
            }
            ++total;
            hits += exterior && interior;
          }
          if (total > 0) best = std::max(best, double(hits) / total);
        }
        return best;
      };
      struct Candidate { int l, t, r, b; double score; };
      std::vector<Candidate> proposals;
      const double target_width = observed_h * expected_aspect;
      if (target_width >= 2 && target_width <= rw) {
        const int candidate_w = int(std::lround(target_width));
        if (candidate_w != observed_w) {
          const int left = maxx - candidate_w + 1;
          if (left >= 0 && left < maxx)
            proposals.push_back({left, miny, maxx, maxy,
                std::min(edge_score(true, true, left, miny, maxy),
                         edge_score(true, false, maxx, miny, maxy))});
          const int right = minx + candidate_w - 1;
          if (right > minx && right < rw)
            proposals.push_back({minx, miny, right, maxy,
                std::min(edge_score(true, false, right, miny, maxy),
                         edge_score(true, true, minx, miny, maxy))});
        }
      }
      const double target_height = observed_w / expected_aspect;
      if (target_height >= 2 && target_height <= rh) {
        const int candidate_h = int(std::lround(target_height));
        if (candidate_h != observed_h) {
          const int top = maxy - candidate_h + 1;
          if (top >= 0 && top < maxy)
            proposals.push_back({minx, top, maxx, maxy,
                std::min(edge_score(false, true, top, minx, maxx),
                         edge_score(false, false, maxy, minx, maxx))});
          const int bottom = miny + candidate_h - 1;
          if (bottom > miny && bottom < rh)
            proposals.push_back({minx, miny, maxx, bottom,
                std::min(edge_score(false, false, bottom, minx, maxx),
                         edge_score(false, true, miny, minx, maxx))});
        }
      }
      std::sort(proposals.begin(), proposals.end(),
                [](const Candidate& a, const Candidate& b) { return a.score > b.score; });
      if (proposals.empty() || proposals[0].score < 0.65 ||
          (proposals.size() > 1 && proposals[1].score > proposals[0].score - 0.10))
        return fail("navigator canvas aspect mismatch without unique supported edge");
      minx = proposals[0].l; miny = proposals[0].t;
      maxx = proposals[0].r; maxy = proposals[0].b;
      std::fill(std::begin(clip_ext), std::end(clip_ext), 0);
    }
  }
  out.bounds_capture={roi.left+minx-clip_ext[0],roi.top+miny-clip_ext[1],
                      roi.left+maxx+1+clip_ext[2],roi.top+maxy+1+clip_ext[3]};
  out.bounds_screen={out.bounds_capture.left+origin_x,out.bounds_capture.top+origin_y,
                     out.bounds_capture.right+origin_x,out.bounds_capture.bottom+origin_y};
  const int bw=out.bounds_capture.width(),bh=out.bounds_capture.height();
  out.aspect_ratio=float(bw)/bh;

  // Require the actual two-sided transition at each proposed edge: canvas on
  // the inside and exterior background immediately outside. Merely having a
  // background band around an arbitrary foreground no longer invents 4 edges.
  const int edge_depth=std::clamp(int(std::lround(2*(std::isfinite(dpi_scale)?dpi_scale:1.f))),1,4);
  for(int side=0;side<4;++side) {
    int hits=0,total=0;
    if(side==0 || side==2) {
      const int inside_x=side==0?minx:maxx;
      const int outside_x=side==0?minx-1:maxx+1;
      if(outside_x>=0&&outside_x<rw) for(int y=miny;y<=maxy;++y) {
        ++total;
        bool inside=false;
        for(int k=0;k<edge_depth;++k) {
          const int x=side==0?inside_x+k:inside_x-k;
          if(x>=0&&x<rw&&canvas_component[index(x,y)]) {inside=true;break;}
        }
        hits+=inside&&exterior[index(outside_x,y)];
      }
    } else {
      const int inside_y=side==1?miny:maxy;
      const int outside_y=side==1?miny-1:maxy+1;
      if(outside_y>=0&&outside_y<rh) for(int x=minx;x<=maxx;++x) {
        ++total;
        bool inside=false;
        for(int k=0;k<edge_depth;++k) {
          const int y=side==1?inside_y+k:inside_y-k;
          if(y>=0&&y<rh&&canvas_component[index(x,y)]) {inside=true;break;}
        }
        hits+=inside&&exterior[index(x,outside_y)];
      }
    }
    out.boundary_support[side]=total>0?float(hits)/total:0.f;
    if(out.boundary_support[side]>=0.90f) out.visible_edges_mask|=1<<side;
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
  // Surrounded foreground and axis-aligned edge support are distinct facts.
  // Direct mapping additionally checks boundary_support; a rotated canvas may
  // be surrounded without having four straight sides on its bounding box.
  out.four_sides_complete=surrounded_sides==4;
  int supported_edges=0;
  for(int side=0;side<4;++side) supported_edges+=(out.visible_edges_mask&(1<<side))?1:0;
  out.confidence=0.35f+0.10f*surrounded_sides+0.0625f*supported_edges;
  return out;
}
}  // namespace sct
