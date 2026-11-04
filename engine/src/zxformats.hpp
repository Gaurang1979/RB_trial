// Code types between the engine (cr_format flags) and zxing-cpp (BarcodeFormat), zxformats.cpp.
#pragma once

#include "codereader.h"

#include <ZXing/ReadBarcode.h>

namespace cr {

// the zxing formats to enable for cr_format flags (CR_ANY: every type the engine knows)
ZXing::BarcodeFormats toZx(int formats);
// the cr_format flag of one zxing read; 0 = a type the engine does not report (rMQR, a lone EAN
// add-on, MaxiCode, ...). Exact: a Micro QR is CR_MICRO_QR, never CR_QR, so a job that enables
// one type never reports a code of a sibling type that zxing reads with the same decoder
int fromZx(ZXing::BarcodeFormat f);
// a read's text without its EAN/UPC add-on (zxing appends it to the text when add-ons are read);
// the add-on's digits go to addon ("" = none)
std::string mainText(const ZXing::Barcode& b, std::string* addon);
// the text of a zxing decode of a module grid the engine's own decoders drew ("" = none), noted with its
// AIM identifier and GS1 flag in the open OwnReadScopes (reader.hpp)
std::string ownGridText(const ZXing::Barcode& b);

} // namespace cr
