# Coordination Change Note: Tool 1 (Locate) — Geometric Shape Model & Pattern Advisor

**Author**: Claude (Pair Programmer with Gaurang)  
**Date**: 2026-11-02  
**Branch**: `claude/tool-1-geometric-locate-and-pattern-advisor`  
**Tool**: Tool 1 — Locate ("Find the part")  
**Backlog Reference**: `TOOL-METHODS-BACKLOG.md` Section 2 & Section 15 (Item 8)  
**Target Repository**: `https://github.com/Gaurang1979/Vision_System.git`  

---

## 1. Summary of Changes

This change upgrades Tool 1 (Locate) from simple correlation to an industrial edge-based geometric shape model framework matching Cognex PatMax and HALCON `find_shape_model` standards:

1. **Shape-Model Pattern Evaluation & Quality Advisor (`PatternQuality`)**:
   - Analyzes Sobel gradient vectors, edge point density, dynamic contrast range, and directional orientation entropy across 8 spatial bins.
   - Computes Shannon entropy ($H = -\sum p_i \log_2 p_i$) to guarantee multi-axial edge distribution, detecting single-axis degenerate patterns (e.g., straight ruler lines that slip along their axis).
   - Generates automated operator advice: `Optimal` (>=85%), `Good` (>=70%), `Fair` (>=50%), or `Degenerate` (<50%) with specific warnings on contrast, edge count, or directional bias.

2. **Polarity Metrics & Search Modes**:
   - Added `LocateMetric` (`UsePolarity`, `IgnoreGlobalPolarity` for dark/light inversion, `IgnoreLocalPolarity` for specular/foil highlights).
   - Added `LocateSearchMode` (`EdgeGeometric` vs `Correlation`).
   - Added pyramid search levels and scale parameters (`MinScale`, `MaxScale`).

3. **Fixture Inverse Transformation (`Fixture.InverseMap`)**:
   - Provides exact analytical reverse Euclidean transformation mapping from camera frame $(X, Y)$ back to pattern teach origin $(x, y)$.

4. **Teach UI Integration (`Teach.razor`)**:
   - Live visual shape model quality card showing rating badge, edge count, entropy score, contrast std dev, and actionable setup warnings.
   - Configurable polarity metric and search mode selectors.

5. **Automated Verification**:
   - Added `dotnet/VisionCore.Tests/LocateToolTests.cs` (9 unit tests).
   - All tests passing 100%.
