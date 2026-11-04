// rules1d.cpp - the job's 1D rules on a decoded text: check digit (Code 39 modulo 43, ITF GS1
// modulo 10, Codabar modulo 16) and length lock. A partial read of a damaged barcode (an ITF scan that stops at a
// spot, a Code 39 missing its last characters) decodes to a valid but shorter text: only the
// length lock and the check digit tell it from the real code.

#include "reader.hpp"

#include <cstring>

namespace cr {

namespace {

constexpr char kCode39Set[] = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%";

// Codabar character values for the modulo 16 check: data 0..15, start / stop characters 16..19
// (A B C D, or their alternates T N * E)
int codabarValue(char c)
{
    static constexpr char kData[] = "0123456789-$:/.+", kEnds[] = "ABCD", kAltEnds[] = "TN*E";
    if (c == '\0') return -1;
    if (const char* at = std::strchr(kData, c)) return static_cast<int>(at - kData);
    if (const char* at = std::strchr(kEnds, c)) return 16 + static_cast<int>(at - kEnds);
    if (const char* at = std::strchr(kAltEnds, c)) return 16 + static_cast<int>(at - kAltEnds);
    return -1;
}

bool isCodabarEnd(char c) { return codabarValue(c) >= 16; }

// where Codabar's check character sits: before the stop character when the text carries it
size_t codabarCheckAt(const std::string& text) { return text.size() - (isCodabarEnd(text.back()) ? 2 : 1); }

} // namespace

bool code39CheckOk(const std::string& text)
{
    if (text.size() < 2) return false;
    int sum = 0;
    for (size_t i = 0; i < text.size(); ++i) {
        // full ASCII text (lower case, control characters) is not the printed symbol: its check
        // digit cannot be verified from the text, so it does not pass
        const char* at = text[i] != '\0' ? std::strchr(kCode39Set, text[i]) : nullptr;
        if (!at) return false;
        const int v = static_cast<int>(at - kCode39Set);
        if (i + 1 < text.size()) sum += v;
        else return sum % 43 == v;
    }
    return false;
}

bool gs1Mod10Ok(const std::string& digits)
{
    if (digits.size() < 2) return false;
    int sum = 0;
    // weights 3, 1, 3, ... from the digit left of the check digit
    for (size_t k = 1; k < digits.size(); ++k) {
        const char c = digits[digits.size() - 1 - k];
        if (c < '0' || c > '9') return false;
        sum += (c - '0') * (k % 2 == 1 ? 3 : 1);
    }
    const char last = digits.back();
    return last >= '0' && last <= '9' && (10 - sum % 10) % 10 == last - '0';
}

bool codabarCheckOk(const std::string& text)
{
    if (text.empty()) return false;
    const bool ends = isCodabarEnd(text.front()) && isCodabarEnd(text.back()) && text.size() >= 2;
    // one data character at least besides the check character
    if (text.size() < (ends ? 4u : 2u)) return false;
    int sum = 0;
    for (size_t i = 0; i < text.size(); ++i) {
        const int v = codabarValue(text[i]);
        // start and stop characters only at the ends, and the check character a data character
        if (v < 0 || (v >= 16 && !(ends && (i == 0 || i + 1 == text.size())))) return false;
        sum += v;
    }
    return sum % 16 == 0;
}

bool readMatchesKept(int format, const std::string& read, const std::string& kept)
{
    if (read == kept) return true;
    if (!(format & (CR_CODE39 | CR_ITF | CR_CODABAR)) || read.size() != kept.size() + 1) return false;
    std::string stripped = read;
    if (!applyLinearRules(format, stripped, 0, 0, CR_CHECK_STRIP)) return false;
    return stripped == kept;
}

int checkDigitState(int format, int checkDigit)
{
    // these decoders verify a check of their own on every read
    if (format & (CR_EAN13 | CR_EAN8 | CR_UPCA | CR_UPCE | CR_GS1_128 | CR_CODE93 | CR_DATABAR)) return 1;
    // optional check digits: verified only when the job asks (a failing read is dropped)
    if (format & (CR_CODE39 | CR_ITF | CR_CODABAR)) return checkDigit != CR_CHECK_AS_READ ? 1 : -1;
    return -1;
}

bool applyLinearRules(int format, std::string& text, int minLength, int maxLength, int checkDigit, int* reject)
{
    if (reject) *reject = kRejectNone;
    if (!(format & CR_ALL_LINEAR)) return true;
    // the length as printed, check digit included, in characters (UTF-8 lead bytes), so a lock taught
    // from a read holds whatever the check-digit setting; EAN and UPC have fixed lengths of their own
    if (!(format & (CR_EAN13 | CR_EAN8 | CR_UPCA | CR_UPCE))) {
        int n = 0;
        for (const char ch : text) n += (static_cast<unsigned char>(ch) & 0xC0) != 0x80;
        if ((minLength > 0 && n < minLength) || (maxLength > 0 && n > maxLength)) {
            if (reject) *reject = kRejectLength;
            return false;
        }
    }
    if ((format & (CR_CODE39 | CR_ITF | CR_CODABAR)) && checkDigit != CR_CHECK_AS_READ) {
        const bool ok = format & CR_CODE39 ? code39CheckOk(text) : format & CR_ITF ? gs1Mod10Ok(text) : codabarCheckOk(text);
        if (!ok) {
            if (reject) *reject = kRejectCheckDigit;
            return false;
        }
        // Codabar keeps its start and stop characters: the check character before the stop goes
        if (checkDigit == CR_CHECK_STRIP) text.erase(format & CR_CODABAR ? codabarCheckAt(text) : text.size() - 1, 1);
    }
    return true;
}

} // namespace cr
