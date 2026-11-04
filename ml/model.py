"""CodeNet: a small fully-convolutional network that marks where 2D codes are.

Input: 1 x H x W grey image at 1/8 of full resolution, stretched to 0..1.
Output: 1 x H/2 x W/2 logits (code / not code), i.e. 1/16 of full resolution.
Depthwise-separable convolutions keep it near 0.2 GMAC for a 20 MP frame (684 x 456 input),
so it runs in tens of milliseconds on an Intel CPU.
"""

import torch
import torch.nn as nn
import torch.nn.functional as F


def conv_bn(i, o, stride=1):
    return nn.Sequential(nn.Conv2d(i, o, 3, stride, 1, bias=False), nn.BatchNorm2d(o), nn.ReLU(inplace=True))


def sep(i, o, stride=1, dilation=1):
    return nn.Sequential(
        nn.Conv2d(i, i, 3, stride, dilation, dilation=dilation, groups=i, bias=False),
        nn.BatchNorm2d(i), nn.ReLU(inplace=True),
        nn.Conv2d(i, o, 1, bias=False), nn.BatchNorm2d(o), nn.ReLU(inplace=True))


class CodeNet(nn.Module):
    def __init__(self, w=16):
        super().__init__()
        self.s2 = nn.Sequential(conv_bn(1, w, 2), sep(w, w))                           # /2
        self.s4 = nn.Sequential(sep(w, 2 * w, 2), sep(2 * w, 2 * w))                   # /4
        self.s8 = nn.Sequential(sep(2 * w, 4 * w, 2), sep(4 * w, 4 * w),               # /8, wide context
                                sep(4 * w, 4 * w, dilation=2), sep(4 * w, 4 * w, dilation=4))
        self.lat8 = nn.Conv2d(4 * w, 2 * w, 1)
        self.fuse4 = sep(2 * w, 2 * w)
        self.lat4 = nn.Conv2d(2 * w, w, 1)
        self.fuse2 = sep(w, w)
        self.head = nn.Conv2d(w, 1, 1)

    def forward(self, x):
        f2 = self.s2(x)
        f4 = self.s4(f2)
        f8 = self.s8(f4)
        u4 = self.fuse4(F.interpolate(self.lat8(f8), size=f4.shape[-2:], mode="nearest") + f4)
        u2 = self.fuse2(F.interpolate(self.lat4(u4), size=f2.shape[-2:], mode="nearest") + f2)
        return self.head(u2)


def stretch(img_u8):
    """Same input normalisation as the C++ engine: 1st..99.5th percentile -> 0..1."""
    import numpy as np
    lo, hi = np.percentile(img_u8, (1, 99.5))
    if hi - lo < 4:
        hi = lo + 4
    return np.clip((img_u8.astype(np.float32) - np.float32(lo)) / np.float32(hi - lo), 0, 1).astype(np.float32)


if __name__ == "__main__":
    m = CodeNet()
    n = sum(p.numel() for p in m.parameters())
    y = m(torch.zeros(1, 1, 456, 684))
    print(f"params {n}, output {tuple(y.shape)}")
