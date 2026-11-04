"""Replays dots.sweep's random sequence to the Data Matrix 'module px' 3.0 level and decodes only
there, to find the scene of the wrong read (the scenes before are drawn but not decoded)."""
import sys, os, numpy as np, zxingcpp, cv2
sys.path.insert(0, os.path.dirname(__file__)); sys.path.insert(0, os.path.expanduser('~/code/Barcode_Reading/ml'))
import dots, engine
from dots_wrong import STEPS
M = os.path.expanduser('~/code/Barcode_Reading/ml/models/')
rng = np.random.default_rng(9)
F = zxingcpp.BarcodeFormat
for label, (key, levels) in dots.LEVELS.items():
    for lv in levels:
        for k in range(4):
            texts = [f'CLUK A 0607{rng.integers(10000, 99999)}' for _ in range(6)]
            img = dots.scene(texts, F.DataMatrix, rng, **{'dot': 0.7, key: lv})
            if label != 'module px' or lv != 3.0: continue
            codes, _ = engine.decode(img, fmt=2, expected=6, budget_ms=500, model=M + 'codenet.onnx', enhancer=M + 'enhancer.onnx')
            for t, q, step, _ in codes:
                if t not in texts:
                    print(f'scene {k}: WRONG "{t}" by {STEPS.get(step, step)} at {q.mean(0).round()}; texts {texts}')
                    cv2.imwrite(sys.argv[1] if len(sys.argv) > 1 else '/tmp/wrong.png', img)
    if label == 'module px': break
print('done')
