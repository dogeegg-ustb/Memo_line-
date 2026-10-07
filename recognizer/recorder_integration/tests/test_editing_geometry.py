import math
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from state_timeline import StateTimeline, semantic_state


class EditingMatrixTests(unittest.TestCase):
    def test_matrix_coefficients_are_published_and_part_of_semantic_state(self):
        matrices = dict(screenToCanvas=[.01, 0, -1, 0, .02, -2],
                        canvasToScreen=[100, 0, 100, 0, 50, 100],
                        convention='sct-affine-row-major/v1', canvasSpace='normalized')
        raw = dict(success=True, canvasOriginScreenPx=dict(x=100, y=100),
                   snapshot=dict(canvasPixelWidth=100, canvasPixelHeight=50, matrices=matrices))
        state = semantic_state('canvasViewState', raw)
        self.assertEqual(state['transform']['matrices'], matrices)
        updates = []
        timeline = StateTimeline(lambda _: None, lambda _: None, lambda *args: updates.append(args[1]))
        timeline.observe('canvasViewState', raw, {})
        matrices['screenToCanvas'][2] -= .1
        timeline.observe('canvasViewState', raw, {})
        self.assertEqual([u['status'] for u in updates], ['changed', 'changed'])

    def test_clockwise_rotation_inverse_and_canvas_origin(self):
        width, height, scale, rotation = 4961, 7016, .302, math.radians(37)
        cosine, sine = math.cos(rotation), math.sin(rotation)
        origin = (1130, -162.85656872093534)
        forward = [scale*width*cosine, -scale*height*sine, origin[0],
                   scale*width*sine, scale*height*cosine, origin[1]]
        backward = [cosine/(scale*width), sine/(scale*width),
                    -(cosine*origin[0]+sine*origin[1])/(scale*width),
                    -sine/(scale*height), cosine/(scale*height),
                    (sine*origin[0]-cosine*origin[1])/(scale*height)]
        apply = lambda m, p: (m[0]*p[0]+m[1]*p[1]+m[2], m[3]*p[0]+m[4]*p[1]+m[5])
        self.assertEqual(apply(forward, (0, 0)), origin)
        for screen in [origin, (1200, 500), (-100, 150), (3000, 1800)]:
            normalized = apply(backward, screen)
            dx, dy = screen[0]-origin[0], screen[1]-origin[1]
            watcher = ((cosine*dx+sine*dy)/scale, (-sine*dx+cosine*dy)/scale)
            self.assertAlmostEqual(normalized[0]*width, watcher[0], delta=.5)
            self.assertAlmostEqual(normalized[1]*height, watcher[1], delta=.5)
            restored = apply(forward, normalized)
            self.assertAlmostEqual(restored[0], screen[0], places=9)
            self.assertAlmostEqual(restored[1], screen[1], places=9)


if __name__ == '__main__':
    unittest.main()
