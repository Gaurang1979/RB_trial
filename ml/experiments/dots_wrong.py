"""Hunt for the wrong read the dot-print sweep showed (Data Matrix, 3 px modules, dot 0.7): many
scenes, every wrong read printed with the step that made it and the true texts nearby."""
import sys, os, numpy as np, zxingcpp
sys.path.insert(0, os.path.dirname(__file__)); sys.path.insert(0, os.path.expanduser('~/code/Barcode_Reading/ml'))
import dots, engine
M = os.path.expanduser('~/code/Barcode_Reading/ml/models/')
STEPS = {0: 'plain', 1: 'upscale', 2: 'clahe', 3: 'sharpen', 4: 'binary', 5: 'ai-enhance', 6: 'flat', 7: 'flat-adapt', 8: 'dmtx', 10: 'full',
         11: 'full-bright', 12: 'deblur', 13: 'deblur2', 14: 'up4', 15: 'gridfit', 16: 'match', 17: 'lattice', 18: 'known', 19: 'glare', 23: 'dots'}
if __name__ == '__main__':
    rng = np.random.default_rng(int(sys.argv[2]) if len(sys.argv) > 2 else 1)
    n = int(sys.argv[1]) if len(sys.argv) > 1 else 20
    wrong = read = 0
    for k in range(n):
        texts = [f'CLUK A 0607{rng.integers(10000, 99999)}' for _ in range(6)]
        img = dots.scene(texts, zxingcpp.BarcodeFormat.DataMatrix, rng, dot=0.7, px=3.0)
        codes, _ = engine.decode(img, fmt=2, expected=6, budget_ms=500, model=M + 'codenet.onnx', enhancer=M + 'enhancer.onnx')
        for t, q, step, _ in codes:
            if t in texts: read += 1; continue
            wrong += 1
            near = min(texts, key=lambda x: sum(a != b for a, b in zip(x, t)) + abs(len(x) - len(t)))
            print(f'scene {k}: WRONG "{t}" by {STEPS.get(step, step)}; nearest true "{near}"', flush=True)
    print(f'{n} scenes: {read} right, {wrong} wrong')
