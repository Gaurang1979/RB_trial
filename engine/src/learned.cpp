// Learned state in and out of a text blob, so a product's reader can start where the last shift
// stopped. The blob carries only what is safe to carry: the step win/try tables (whole image and
// per zone), the zone read map, the learned QR and Data Matrix sizes, the cost of the whole-image
// passes and the printer's encoding as a hint that the first decoded code must confirm. The code
// size range and the symbols per code are left out on purpose: they re-learn within one case, and
// stale values drop regions.
//
// kVersion must be bumped whenever a CR_STEP id changes meaning or the zone layout changes: the
// tables are indexed by step id, and a blob of another version is rejected.
//
// On load the tables are scaled down (ratios kept) so that no step holds more than kTriesCap
// tries (kZoneTriesCap per zone): a step pruned under old lighting can be found again by the
// probe cases within a few of them, instead of needing a per cent of months of history. The zone
// read map and the case count are per run and start at zero.
//
// Format (one key per line; unknown keys are ignored so a caller may add its own, such as the
// station's product key):
//   codereader-learned 1
//   steps 24 zones 4
//   cases N
//   wins v0 .. v23
//   tries v0 .. v23
//   zonewins z v0 .. v23        (one line per zone, row-major)
//   zonetries z v0 .. v23
//   zoneread v0 .. v15
//   zoneunread v0 .. v15
//   qr modules
//   dm modules side
//   slots rows cols               (the case's slot lattice, either way round; 0 0 = unknown)
//   fullpass ms
//   psf all z0 .. z15            (lens blur, sigma in modules, overall and per zone)
//   enc kind split ecl modules format
#include "reader.hpp"

#include <cmath>
#include <sstream>

namespace cr {

namespace {

constexpr const char* kHeader = "codereader-learned";
constexpr int kVersion = 1;
constexpr int kZ = StepStats::kZones * StepStats::kZones;
constexpr uint32_t kTriesCap = 200, kZoneTriesCap = 60;

// halves wins and tries together until tries <= cap: the win rate stays, the weight of history shrinks
void scaleDown(uint32_t* wins, uint32_t* tries, int n, uint32_t cap)
{
    for (int i = 0; i < n; ++i)
        while (tries[i] > cap) {
            tries[i] = (tries[i] + 1) / 2;
            wins[i] = wins[i] / 2;
        }
}

bool fail(std::string* error, const std::string& what)
{
    if (error) *error = what;
    return false;
}

} // namespace

std::string learnedToText(const StepStats& s)
{
    std::ostringstream o;
    o << kHeader << ' ' << kVersion << '\n';
    o << "steps " << StepStats::kMax << " zones " << StepStats::kZones << '\n';
    o << "cases " << s.cases.load() << '\n';
    o << "wins";
    for (const auto& v : s.wins) o << ' ' << v.load();
    o << "\ntries";
    for (const auto& v : s.tries) o << ' ' << v.load();
    o << '\n';
    for (int z = 0; z < kZ; ++z) {
        o << "zonewins " << z;
        for (const auto& v : s.zoneWins[z]) o << ' ' << v.load();
        o << "\nzonetries " << z;
        for (const auto& v : s.zoneTries[z]) o << ' ' << v.load();
        o << '\n';
    }
    o << "zoneread";
    for (const auto& v : s.zoneRead) o << ' ' << v.load();
    o << "\nzoneunread";
    for (const auto& v : s.zoneUnread) o << ' ' << v.load();
    o << '\n';
    o << "qr " << s.qrModules.load() << '\n';
    o << "dm " << s.dmModules.load() << ' ' << s.dmSide.load() << '\n';
    o << "slots " << s.slotA.load() << ' ' << s.slotB.load() << '\n';
    o << "polarity " << s.polarity.load() << '\n';
    o << "fullpass " << s.fullPassMs.load() << '\n';
    o << "psf " << s.psfSigmaAll.load();
    for (const auto& v : s.psfSigma) o << ' ' << v.load();
    o << '\n';
    o << "enc " << s.encKind.load() << ' ' << s.encSplit.load() << ' ' << s.encEcl.load() << ' '
      << s.encModules.load() << ' ' << s.encFormat.load() << '\n';
    return o.str();
}

bool learnedFromText(StepStats& s, const std::string& text, std::string* error)
{
    std::istringstream in(text);
    std::string line, key;
    int version = 0;
    if (!std::getline(in, line)) return fail(error, "learned state is empty");
    {
        std::istringstream l(line);
        if (!(l >> key >> version) || key != kHeader) return fail(error, "not a learned-state blob");
        if (version != kVersion) return fail(error, "learned state version " + std::to_string(version) + " is not " + std::to_string(kVersion));
    }
    // parsed into plain arrays first: a blob rejected halfway leaves the reader untouched
    uint32_t cases = 0;
    std::array<uint32_t, StepStats::kMax> wins{}, tries{};
    std::array<std::array<uint32_t, StepStats::kMax>, kZ> zoneWins{}, zoneTries{};
    std::array<uint32_t, kZ> zoneRead{}, zoneUnread{};
    int qr = 0, dm = 0, encKind = -1, encSplit = 0, encEcl = 1, encModules = 0, encFormat = CR_QR;
    int polarity = 0;
    int slotA = 0, slotB = 0;
    float dmSide = 0.f, fullPass = 0.f;
    float psfAll = 0.f;
    std::array<float, kZ> psf{};
    const auto blurOk = [](float v) { return std::isfinite(v) && v > 0.f && v <= 2.f ? v : 0.f; };
    bool sawSteps = false;
    const auto readN = [&](std::istringstream& l, uint32_t* out, int n, const std::string& what) {
        for (int i = 0; i < n; ++i) {
            long long v = 0;
            if (!(l >> v) || v < 0 || v > 0xFFFFFFFFll) return fail(error, what + ": " + std::to_string(n) + " counts expected");
            out[i] = static_cast<uint32_t>(v);
        }
        return true;
    };
    while (std::getline(in, line)) {
        std::istringstream l(line);
        if (!(l >> key)) continue;
        if (key == "steps") {
            int steps = 0, zones = 0;
            std::string zk;
            if (!(l >> steps >> zk >> zones) || zk != "zones") return fail(error, "steps line");
            if (steps != StepStats::kMax || zones != StepStats::kZones)
                return fail(error, "learned state is for another engine (" + std::to_string(steps) + " steps, " + std::to_string(zones) + " zones)");
            sawSteps = true;
        } else if (key == "cases") {
            long long v = 0;
            if (!(l >> v) || v < 0) return fail(error, "cases line");
            cases = static_cast<uint32_t>(std::min(v, 0xFFFFFFFFll));
        } else if (key == "wins") {
            if (!readN(l, wins.data(), StepStats::kMax, "wins")) return false;
        } else if (key == "tries") {
            if (!readN(l, tries.data(), StepStats::kMax, "tries")) return false;
        } else if (key == "zonewins" || key == "zonetries") {
            int z = -1;
            if (!(l >> z) || z < 0 || z >= kZ) return fail(error, key + ": zone 0.." + std::to_string(kZ - 1) + " expected");
            if (!readN(l, (key == "zonewins" ? zoneWins : zoneTries)[z].data(), StepStats::kMax, key)) return false;
        } else if (key == "zoneread") {
            if (!readN(l, zoneRead.data(), kZ, key)) return false;
        } else if (key == "zoneunread") {
            if (!readN(l, zoneUnread.data(), kZ, key)) return false;
        } else if (key == "qr") {
            if (!(l >> qr)) return fail(error, "qr line");
            if (qr != 0 && (qr < 21 || qr > 177 || (qr - 21) % 4 != 0)) qr = 0;
        } else if (key == "polarity") {
            if (!(l >> polarity)) return fail(error, "polarity line");
            if (polarity < 0 || polarity > 2) polarity = 0;
        } else if (key == "slots") {
            if (!(l >> slotA >> slotB)) return fail(error, "slots line");
            if (slotA < 2 || slotB < 2 || slotA > 40 || slotB > 40) slotA = slotB = 0;
        } else if (key == "dm") {
            if (!(l >> dm >> dmSide)) return fail(error, "dm line");
            if (dm != 0 && (dm < 10 || dm > 26 || dm % 2 != 0)) dm = 0;
            if (!std::isfinite(dmSide) || dmSide <= 0.f || dmSide > 1e5f) dmSide = 0.f;
            if (dm == 0 || dmSide == 0.f) dm = 0, dmSide = 0.f;
        } else if (key == "fullpass") {
            if (!(l >> fullPass)) return fail(error, "fullpass line");
            if (!std::isfinite(fullPass) || fullPass < 0.f || fullPass > 1e5f) fullPass = 0.f;
        } else if (key == "psf") {
            if (!(l >> psfAll)) return fail(error, "psf line");
            psfAll = blurOk(psfAll);
            for (auto& v : psf) {
                if (!(l >> v)) return fail(error, "psf: " + std::to_string(kZ + 1) + " values expected");
                v = blurOk(v);
            }
        } else if (key == "enc") {
            if (!(l >> encKind >> encSplit >> encEcl >> encModules >> encFormat)) return fail(error, "enc line");
            // only an encoding the matchers can render: a QR of 21 + 4k modules with a kind 0..4 and an
            // EC level 0..3, or an ASCII Data Matrix (kind 0) of an even 10..26; anything else is unknown
            const bool qrOk = encFormat == CR_QR && encModules >= 21 && encModules <= 177 && (encModules - 21) % 4 == 0 && encKind >= 0 && encKind <= 4 && encEcl >= 0 && encEcl <= 3 && encSplit >= 0 && encSplit <= encModules * encModules;
            const bool dmOk = encFormat == CR_DATAMATRIX && encModules >= 10 && encModules <= 26 && encModules % 2 == 0 && encKind >= 0 && encKind <= 1;
            if (!qrOk && !dmOk) encKind = -1, encSplit = 0, encEcl = 1, encModules = 0, encFormat = CR_QR;
        }
        // any other key: another writer's line, left alone
    }
    if (!sawSteps) return fail(error, "learned state has no steps line");
    // a step cannot have won more often than it was tried; old history weighs no more than the cap
    for (int i = 0; i < StepStats::kMax; ++i) wins[i] = std::min(wins[i], tries[i]);
    scaleDown(wins.data(), tries.data(), StepStats::kMax, kTriesCap);
    for (int z = 0; z < kZ; ++z) {
        for (int i = 0; i < StepStats::kMax; ++i) zoneWins[z][i] = std::min(zoneWins[z][i], zoneTries[z][i]);
        scaleDown(zoneWins[z].data(), zoneTries[z].data(), StepStats::kMax, kZoneTriesCap);
    }
    (void)cases; (void)zoneRead; (void)zoneUnread; // per run: read back for the record, not restored

    s.cases.store(0);
    for (int i = 0; i < StepStats::kMax; ++i) {
        s.wins[i].store(wins[i]);
        s.tries[i].store(tries[i]);
    }
    for (int z = 0; z < kZ; ++z) {
        for (int i = 0; i < StepStats::kMax; ++i) {
            s.zoneWins[z][i].store(zoneWins[z][i]);
            s.zoneTries[z][i].store(zoneTries[z][i]);
        }
        s.zoneRead[z].store(0);
        s.zoneUnread[z].store(0);
    }
    s.qrModules.store(qr);
    s.dmModules.store(dm);
    s.dmSide.store(dmSide);
    s.slotA.store(slotA);
    s.polarity.store(polarity);
    s.slotB.store(slotB);
    s.fullPassMs.store(fullPass);
    s.psfSigmaAll.store(psfAll);
    for (size_t z = 0; z < psf.size(); ++z) s.psfSigma[z].store(psf[z]);
    s.encFormat.store(encFormat);
    s.encSplit.store(encSplit);
    s.encEcl.store(encEcl);
    s.encModules.store(encModules);
    s.encKind.store(encKind);
    s.encConfirmed.store(0); // a loaded encoding is a hint until a decoded code confirms it
    return true;
}

} // namespace cr
