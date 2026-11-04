"""OcrNet: a small convolution-only CTC line recogniser.

Input: 1 x 1 x 32 x W, a straightened line (text dark on light) scaled to 0..1, W a multiple of 4.
Output: 1 x C x 1 x W/4 logits, C = 1 (CTC blank) + characters in models/ocr_charset.txt.

No recurrent layers: OpenCV's dnn module (the C++ engine's runtime) runs plain convolutions reliably
and fast. The temporal context an LSTM would give comes from dilated 1 x 3 convolutions along the line.
"""

import torch
import torch.nn as nn


def cbr(i, o, k=3, stride=1, groups=1, dilation=(1, 1), pad=None):
    if pad is None:
        pad = (dilation[0] * (k if isinstance(k, int) else k[0]) // 2, dilation[1] * (k if isinstance(k, int) else k[1]) // 2)
    return nn.Sequential(nn.Conv2d(i, o, k, stride, pad, dilation=dilation, groups=groups, bias=False),
                         nn.BatchNorm2d(o), nn.ReLU(inplace=True))


class Sep(nn.Module):
    """Depthwise-separable block with a residual when the shape allows it."""

    def __init__(self, i, o, stride=(1, 1)):
        super().__init__()
        self.dw = cbr(i, i, 3, stride, groups=i)
        self.pw = cbr(i, o, 1, pad=(0, 0))
        self.res = i == o and stride == (1, 1)

    def forward(self, x):
        y = self.pw(self.dw(x))
        return x + y if self.res else y


class Temporal(nn.Module):
    def __init__(self, c, d):
        super().__init__()
        self.conv = cbr(c, c, (1, 3), dilation=(1, d), pad=(0, d))

    def forward(self, x):
        return x + self.conv(x)


class OcrNet(nn.Module):
    def __init__(self, n_classes, w=32):
        super().__init__()
        self.body = nn.Sequential(
            cbr(1, w),                       # 32 x W
            Sep(w, 2 * w, (2, 2)),           # 16 x W/2
            Sep(2 * w, 2 * w),
            Sep(2 * w, 3 * w, (2, 2)),       # 8 x W/4
            Sep(3 * w, 3 * w),
            Sep(3 * w, 4 * w, (2, 1)),       # 4 x W/4
            Sep(4 * w, 4 * w),
            Sep(4 * w, 4 * w, (2, 1)),       # 2 x W/4
            cbr(4 * w, 6 * w, (2, 1), pad=(0, 0)),  # 1 x W/4
        )
        self.context = nn.Sequential(Temporal(6 * w, 1), Temporal(6 * w, 2), Temporal(6 * w, 4))
        self.drop = nn.Dropout2d(0.1)
        self.head = nn.Conv2d(6 * w, n_classes, 1)

    def forward(self, x):
        return self.head(self.drop(self.context(self.body(x))))


if __name__ == "__main__":
    m = OcrNet(79)
    print("params", sum(p.numel() for p in m.parameters()))
    print(m(torch.zeros(2, 1, 32, 320)).shape)
