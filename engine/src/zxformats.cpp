// zxformats.cpp - cr_format flags to and from zxing-cpp formats (zxing-cpp 3: a format is a
// symbology plus a variant, and enabling a symbology enables all its variants).

#include "zxformats.hpp"
#include "reader.hpp"

#include <algorithm>
#include <vector>

namespace cr {

ZXing::BarcodeFormats toZx(int f)
{
    using F = ZXing::BarcodeFormat;
    std::vector<F> z;
    // QR enables zxing's whole QR family (Micro QR and rMQR too): a read of a type the job did not
    // enable is dropped after fromZx, so an rMQR never counts in a QR job
    if (f & (CR_QR | CR_MICRO_QR | CR_RMQR)) z.push_back(F::QRCode);
    if (f & CR_DATAMATRIX) z.push_back(F::DataMatrix);
    if (f & CR_GS1_128) z.push_back(F::Code128);
    if (f & CR_EAN13) z.push_back(F::EAN13);
    if (f & CR_EAN8) z.push_back(F::EAN8);
    if (f & CR_UPCA) z.push_back(F::UPCA);
    if (f & CR_UPCE) z.push_back(F::UPCE);
    // standard and full ASCII only: the Code 32 and PZN readers would turn a pharma code's printed
    // text into its number, so a Code 39 job's expected text would not match what is printed
    if (f & CR_CODE39) { z.push_back(F::Code39Std); z.push_back(F::Code39Ext); }
    if (f & CR_CODE93) z.push_back(F::Code93);
    if (f & CR_ITF) z.push_back(F::ITF);
    if (f & CR_CODABAR) z.push_back(F::Codabar);
    if (f & CR_DATABAR) z.push_back(F::DataBar);
    if (f & CR_PDF417) z.push_back(F::PDF417);
    if (f & CR_AZTEC) z.push_back(F::Aztec);
    return ZXing::BarcodeFormats(std::move(z));
}

int fromZx(ZXing::BarcodeFormat f)
{
    using F = ZXing::BarcodeFormat;
    const F s = ZXing::Symbology(f);
    if (s == F::QRCode) {
        if (f == F::MicroQRCode) return CR_MICRO_QR;
        if (f == F::RMQRCode) return CR_RMQR;
        return CR_QR; // the base or Model 1 / Model 2
    }
    if (s == F::DataMatrix) return CR_DATAMATRIX;
    if (s == F::Code128) return CR_GS1_128;
    if (s == F::EANUPC) {
        if (f == F::EAN13 || f == F::ISBN) return CR_EAN13;
        if (f == F::EAN8) return CR_EAN8;
        if (f == F::UPCA) return CR_UPCA;
        if (f == F::UPCE) return CR_UPCE;
        return 0; // EAN-2 / EAN-5 add-on on its own, or the bare family
    }
    if (s == F::Code39) return CR_CODE39;
    if (s == F::Code93) return CR_CODE93;
    if (s == F::ITF) return CR_ITF;
    if (s == F::Codabar) return CR_CODABAR;
    if (s == F::DataBar) return CR_DATABAR;
    if (s == F::PDF417) return CR_PDF417;
    if (s == F::Aztec) return CR_AZTEC;
    return 0;
}

std::string mainText(const ZXing::Barcode& b, std::string* addon)
{
    std::string text = b.text();
    std::string a = b.extra(ZXing::BarcodeExtra::EanAddOn);
    if (!a.empty() && text.size() > a.size() && text.compare(text.size() - a.size(), a.size(), a) == 0)
        text.resize(text.size() - a.size());
    else
        a.clear();
    if (addon) *addon = a;
    return text;
}

namespace {
thread_local OwnReadScope* tlTop = nullptr;
}

OwnReadScope::OwnReadScope() : outer_(tlTop) { tlTop = this; }

OwnReadScope::~OwnReadScope() { tlTop = outer_; }

void OwnReadScope::note(const std::string& text, const std::string& symId, int gs1)
{
    if (text.empty()) return;
    for (OwnReadScope* s = tlTop; s; s = s->outer_) {
        auto it = std::find_if(s->seen_.begin(), s->seen_.end(), [&](const auto& e) { return e.first == text; });
        if (it == s->seen_.end()) s->seen_.push_back({text, {symId, gs1}});
        // the same text decoded with and without FNC1: which one this symbol is, is not known
        else if (it->second.symId != symId) it->second = {};
    }
}

void OwnReadScope::apply(Code& c) const
{
    if (!c.symbologyId.empty()) return;
    for (const auto& [text, s] : seen_)
        if (text == c.text) {
            c.symbologyId = s.symId;
            c.gs1 = s.symId.empty() ? -1 : s.gs1;
            return;
        }
}

std::string ownGridText(const ZXing::Barcode& b)
{
    if (!b.isValid()) return {};
    std::string text = b.text();
    OwnReadScope::note(text, b.symbologyIdentifier(), b.contentType() == ZXing::ContentType::GS1 ? 1 : 0);
    return text;
}

} // namespace cr
