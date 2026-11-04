# Master Guide: Infrared Vision Inspection & Defect Analysis
## Near-Infrared (No-IR-Filter CMOS) & Short-Wave Infrared (SWIR) Industrial Applications

This master engineering guide details industrial infrared machine vision using:
1. **CMOS Cameras with IR-Cut Filter Removed ("NoIR" / NIR 750 nm – 1050 nm)** paired with active 850 nm / 940 nm LED strobe illumination.
2. **Dedicated Short-Wave Infrared (SWIR 900 nm – 1700 nm)** InGaAs sensor cameras.
3. **Mid-Wave & Long-Wave Thermal Infrared (MWIR / LWIR 3 – 14 $\mu\text{m}$)** cameras.

---

## 1. Optical Physics & Spectrum Overview

```mermaid
flowchart LR
    VIS["Visible (VIS)<br>400 - 700 nm<br>Human Eye / Standard Camera"]
    NIR["Near-Infrared (NIR)<br>750 - 1050 nm<br>Standard Silicon CMOS (No-IR Filter)"]
    SWIR["Short-Wave IR (SWIR)<br>900 - 1700 nm<br>InGaAs Focal Plane Sensor"]
    MWIR_LWIR["Thermal IR (MWIR / LWIR)<br>3 - 14 µm<br>Microbolometer (Radiometric)"]

    VIS -->|"Remove IR-cut filter<br>Add 850/940nm LED"| NIR
    NIR -->|"InGaAs Sensor<br>Water 1450nm Absorption"| SWIR
    SWIR -->|"Thermal Emissivity<br>Heat Conduction"| MWIR_LWIR
```

### Physical Principles

| Spectral Band | Wavelength Range | Typical Sensor | Optical Transmission / Absorption Phenonemon | Cost Profile |
|---|:---:|:---:|---|:---:|
| **Visible (VIS)** | $400 - 700\text{ nm}$ | Standard Silicon CMOS | Human color perception; blocked by dark dyes, amber glass, smoke | Standard ($\$200 - \$600$) |
| **Near-IR (NIR / NoIR)** | $750 - 1050\text{ nm}$ | Silicon CMOS with IR-cut glass removed | **High transmission through amber/green/dark PET/glass**; penetrates smoke/haze ($1/\lambda^4$); organic dyes transparent; carbon black absorbs | **Very Low ($\$250 - \$800$)** |
| **Short-Wave IR (SWIR)** | $900 - 1700\text{ nm}$ | InGaAs (Indium Gallium Arsenide) | **Massive water absorption peak at $1450\text{ nm}$**; **silicon wafers 100% transparent**; polymer hydrocarbon discrimination | Moderate-High ($\$3,500 - \$9,000$) |
| **Thermal (LWIR)** | $8 - 14\ \mu\text{m}$ | VOx / a-Si Microbolometer | Sub-surface thermal conduction, platen heater seal profiles, frictional hotspots | Moderate ($\$1,500 - \$4,500$) |

---

## 2. Comprehensive Defect Analysis by Industry

### A. Pharmaceutical & Medical Packaging

```mermaid
graph TD
    subgraph Pharma_Infrared ["Pharma Infrared Inspections"]
        AmberBottles["1. Amber/Brown Glass & PET Bottles<br>(NIR 850-940nm Backlight)"]
        AmberBottles --> P1["Under-fill & Over-fill Level Detection"]
        AmberBottles --> P2["Floating Foreign Glass/Fiber Particulates"]
        AmberBottles --> P3["Cap Liner Seal Compression Integrity"]

        Metameric["2. Anti-Counterfeiting & Security Inks<br>(Dual VIS / NIR Comparison)"]
        Metameric --> M1["Carbon-Black vs Organic Metameric Ink Separation"]
        Metameric --> M2["Barcode Read Through Colored Masking Inks"]

        BlisterLiquid["3. Blister Seam Liquid Ingress<br>(SWIR 1450nm Water Absorption)"]
        BlisterLiquid --> S1["Trapped Liquid Drops in Flange Seam"]
        BlisterLiquid --> S2["Foil Pinhole & Moisture Barrier Leaks"]
    end
```

#### 1. Amber / Dark Glass & PET Medicine Bottles
- **The Problem**: Cough syrups, injectables, and vitamin tinctures are packaged in dark amber or cobalt blue glass to protect photo-sensitive active pharmaceutical ingredients (APIs) from ultraviolet degradation. In visible light, the container is nearly opaque, making optical fill-level and particulate inspection impossible.
- **The Infrared Solution**: Amber glass and brown PET transmit **$> 85\%$ of Near-Infrared light** ($850\text{ nm} - 940\text{ nm}$). By positioning an 850 nm NIR flat back-light behind the bottle, the container turns completely translucent.
- **Defects Detected**:
  - **Fill Level**: Sub-millimeter meniscus detection.
  - **Foreign Particulate Matter (FPM)**: Glass shards, rubber stopper cores, cellulose fibers, and hair block NIR light, appearing as sharp black silhouettes inside the bright liquid.
  - **Cap Liner Seating**: Detects folded or missing inductive liners through the plastic cap threads.

#### 2. Metameric Security Inks & Tamper-Evident Anti-Counterfeiting
- **The Problem**: Counterfeiters replicate commercial packaging using consumer inkjet/toner printers that match visible colors perfectly.
- **The Infrared Solution**: Metameric ink pairs look identical in visible light (e.g. both look dark black or deep red), but under 850/940 nm NIR:
  - **Carbon-black pigment** strongly absorbs NIR (stays jet black).
  - **Organic synthetic dyes** become completely transparent in NIR.
- **Defects Detected**:
  - Missing anti-counterfeit micro-text.
  - Barcodes printed beneath decorative colored logos decoded with $100\%$ contrast without removing outer graphics.

#### 3. Blister Pack Seam Liquid Ingress (SWIR 1450 nm)
- **The Problem**: Liquid oral dosage forms or high-viscosity gels trapped in the perimeter knurled flange ruin the hermetic moisture barrier.
- **The Infrared Solution**: Liquid water has a massive absorption spike at **$1450\text{ nm}$**. Under SWIR backlighting, trapped liquid turns pitch black against the bright reflective aluminum lidding foil.

---

### B. Food, Beverage & Fast-Moving Consumer Goods (FMCG)

```mermaid
graph TD
    subgraph FMCG_Infrared ["FMCG & Beverage Inspections"]
        BeverageBottles["1. Dark Glass Beer & Wine Bottles<br>(NIR 850nm / 940nm)"]
        BeverageBottles --> B1["Liquid Meniscus Level (Ignores Foam)"]
        BeverageBottles --> B2["Under-Cap Cork & Crown Cork Ingress"]

        FoodMoisture["2. Moisture & Contaminant Sorting<br>(SWIR 1450nm / NIR)"]
        FoodMoisture --> F1["Sub-surface Fruit Bruising (Cell Rupture)"]
        FoodMoisture --> F2["Plastic/Wood Inclusions in Dry Grains"]
        FoodMoisture --> F3["Wet Channel Leaks in Retort Pouches"]

        DustPenetration["3. Dust & Smoke Penetration<br>(Rayleigh Scattering 1/λ^4)"]
        DustPenetration --> D1["Bagging Station Seal Check Through Flour/Milk Dust"]
    end
```

#### 1. Fill-Level & Foam/Headspace Separation in Green & Amber Bottles
- **The Problem**: Dark green wine bottles, amber beer bottles, and dark brown soy sauce containers cannot be inspected reliably with white light. Furthermore, high-speed bottling lines generate surface foam that confuses visible edge detectors.
- **The Infrared Solution**:
  - Dark green/amber glass is transparent to NIR (850 nm / 940 nm).
  - Foam consists of thin microscopic air bubbles that scatter NIR light diffusely, while pure liquid absorbs NIR predictably.
  - The software extracts the true liquid-gas meniscus interface, completely ignoring up to $25\text{ mm}$ of surface foam!

#### 2. Sub-Surface Fruit & Produce Bruising (NIR 850 nm Reflection)
- **The Problem**: Impact bruises on apples, avocados, peaches, and citrus do not show skin discoloration until 12–24 hours after the drop, long after packing.
- **The Infrared Solution**: Healthy plant cellular structures create strong diffuse NIR backscattering (the *Wood Effect*). When an impact ruptures internal plant cell walls, fluid leaks into intercellular voids, drastically increasing NIR absorption. Bruises appear as distinct dark blotches within minutes of impact.

#### 3. Dust & Steam Penetration in Bagging Stations (Rayleigh Scattering)
- **The Problem**: Powdered milk, flour, cement, and detergent filling stations create airborne clouds of dust and steam that blind visible cameras.
- **The Infrared Solution**: Scattering cross-section is inversely proportional to the 4th power of wavelength ($\propto 1/\lambda^4$). NIR ($940\text{ nm}$) scatters significantly less than blue/green visible light ($450\text{ nm}$), penetrating heavy particulate dust to inspect seal integrity before cartons close.

---

### C. Electronics, Semiconductor & Solar Cells

```mermaid
graph TD
    subgraph Semi_Infrared ["Semiconductor & Solar Cell Inspections"]
        SiliconSeeThrough["1. Silicon Wafer See-Through<br>(SWIR > 1150nm / InGaAs)"]
        SiliconSeeThrough --> S1["Internal Die Attach Voids & Delamination"]
        SiliconSeeThrough --> S2["Sub-surface Micro-Cracks & Chipping"]
        SiliconSeeThrough --> S3["MEMS Cavity & Hermetic Cap Seal Integrity"]

        SolarEL["2. Solar Cell Electroluminescence (EL)<br>(NoIR CMOS 950-1050nm)"]
        SolarEL --> E1["Inactive Finger Busbars & Inactive Grains"]
        SolarEL --> E2["Dendritic Micro-Cracks & Shunt Shorts"]
    end
```

#### 1. Silicon Wafer Internal Inspection (SWIR $> 1150\text{ nm}$)
- **The Physics**: Pure crystalline silicon has an indirect optical bandgap of $1.12\text{ eV}$, corresponding to a cut-off wavelength of $\lambda \approx 1100\text{ nm}$. At wavelengths above $1150\text{ nm}$, silicon becomes as transparent as clear glass!
- **Defects Detected**:
  - Sub-surface micro-cracks before slicing into dies.
  - Solder void ratio under bonded silicon flip-chips.
  - Hermetic cavity seal width on MEMS pressure and gyroscopic sensors.

#### 2. Photovoltaic (PV) Solar Cell Electroluminescence (EL)
- **The Physics**: When a DC forward bias current is driven through a silicon solar cell, radiative recombination of electron-hole pairs emits near-infrared photons peaking at $\approx 1150\text{ nm}$ with a tail extending down to $950 - 1050\text{ nm}$.
- **Hardware**: A standard high-sensitivity monochrome CMOS camera without an IR-cut filter (equipped with a 950 nm long-pass filter) captures this electroluminescence in a darkened enclosure.
- **Defects Detected**:
  - Micro-cracks appear as stark black cracks (isolated regions cannot conduct current and stay dark).
  - Broken front finger grid lines.
  - Potential-Induced Degradation (PID) and crystal dislocation clusters.

---

## 3. Vision Software Engine Architecture (`vs_infrared`)

To address all these industrial applications on standard CPU industrial fanless PCs without requiring external GPU hardware, we design the **`vs_infrared`** engine with 5 core modules:

```mermaid
graph TD
    subgraph vs_infrared_Engine ["vs_infrared Native Architecture"]
        Input["Camera Frame (8-bit Mono8 / 16-bit Mono16)"]
        Input --> Mod1["1. Meniscus & See-Through Liquid Level Engine"]
        Input --> Mod2["2. Metameric Normalized Difference Ratio (NDMI)"]
        Input --> Mod3["3. Sub-Surface Moisture & Seam Ingress Detector"]
        Input --> Mod4["4. Multi-Scale Gabor / Hessian Crack Extractor"]
        Input --> Mod5["5. Liquid Suspension Particulate Matter Analyzer"]

        Mod1 --> Out1["Fill Level mm + Over/Underfill Diagnostic"]
        Mod2 --> Out2["Metameric Security Pass/Fail Mask"]
        Mod3 --> Out3["Seam Moisture Void Channel Alert"]
        Mod4 --> Out4["Solar EL / Silicon Crack Skeleton"]
        Mod5 --> Out5["FPM Particulate Count & Area"]
    end
```

### Module 1: See-Through Liquid Fill Level & Meniscus Peak Profiler
- **Algorithm**:
  1. Computes vertical average intensity profile across ROI: $P(y) = \frac{1}{W} \sum_{x} I(x, y)$.
  2. Applies 1D Gaussian smoothing derivative $\partial P / \partial y$.
  3. Locates global inflection peak corresponding to the liquid-gas meniscus.
  4. Filters out foam layer by asserting contrast boundary sharpness $S = \max(\partial P / \partial y) / \sigma_{\text{foam}}$.
  5. Converts detected pixel position to physical millimeters via factory calibrated scaling factor: $H_{\text{mm}} = (y_{\text{datum}} - y_{\text{meniscus}}) \cdot \text{scale}$.

### Module 2: Normalized Difference Metameric Index (NDMI)
- **Algorithm**:
  Computes pixel-wise normalized difference between NIR and VIS strobe frames:
  $$\text{NDMI}(x, y) = \frac{I_{\text{NIR}}(x, y) - I_{\text{VIS}}(x, y)}{I_{\text{NIR}}(x, y) + I_{\text{VIS}}(x, y) + 1.0}$$
  - Pixels with $\text{NDMI} > +0.35$ represent organic dye inks that disappear in NIR (decorative graphics).
  - Pixels with $|\text{NDMI}| < 0.15$ represent carbon-black pigment or genuine security ink.

### Module 3: Moisture Barrier Breach & Seam Contamination
- **Algorithm**:
  1. Evaluates seal seam flange along package perimeter.
  2. Measures localized absorption dip $D(u) = I_{\text{nominal}} - I_{\text{measured}}(u)$.
  3. Verifies continuous intact barrier width: if any cold moisture channel reduces residual seal width below minimum specification (e.g. $< 2.0\text{ mm}$), triggers immediate fail-safe NG.

### Module 4: Multi-Scale Tubeness / Hessian Crack Extractor
- **Algorithm**:
  For solar cell EL and silicon wafer fracture detection, constructs the 2D Hessian matrix at each pixel:
  $$H(x, y) = \begin{bmatrix} \frac{\partial^2 I}{\partial x^2} & \frac{\partial^2 I}{\partial x \partial y} \\ \frac{\partial^2 I}{\partial x \partial y} & \frac{\partial^2 I}{\partial y^2} \end{bmatrix}$$
  The maximum eigenvalue $\lambda_{\max}$ responds specifically to 1D curvilinear dark veins (cracks), completely rejecting flat crystal grain intensity shifts.

---

## 4. Hardware Selection & Strobe Lighting Recipes

| Application | Recommended Camera | Wavelength Filter | Illumination Technique |
|---|---|:---:|---|
| **Amber Liquid Fill Level** | Standard Industrial Mono CMOS (No IR filter) | $850\text{ nm}$ Bandpass ($\pm 25\text{ nm}$) | $850\text{ nm}$ High-Power LED Backlight |
| **Dark Beer/Wine Foam Check** | Standard Industrial Mono CMOS (No IR filter) | $940\text{ nm}$ Bandpass | Collimated $940\text{ nm}$ NIR Backlight |
| **Metameric Security Inks** | Dual-Strobe Mono CMOS or Color camera | Dual: VIS Strobe + $850\text{ nm}$ NIR Strobe | Coaxial On-Axis $850\text{ nm}$ + White LED |
| **Pouch Seam Moisture Ingress** | SWIR InGaAs Camera ($640 \times 512$) | $1450\text{ nm}$ Narrow Bandpass | $1450\text{ nm}$ SWIR High-Power Strobe Bar |
| **Solar Cell EL Micro-Cracks** | High-QE NIR Mono CMOS (Sony IMX990 / Pregius) | $950\text{ nm}$ Long-Pass Filter | Forward Electric Bias (Dark Chamber) |
| **Silicon Chip Internal Bonding** | SWIR InGaAs Camera ($1280 \times 1024$) | $1200\text{ nm}$ Long-Pass Filter | Transmissive Tungsten-Halogen or $1300\text{ nm}$ LED |
