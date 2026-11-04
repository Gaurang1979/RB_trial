# Audit fixes: signed and bounded .vsrecipe import, live Run screen values, camouflage labels (Req 17.11 AUD-06/07/09)

- Status: done
- Session: audit-recipe-run-labels, 2026-11-03
- Pull request: #270
- Section 5 item: new
- Components/files: dotnet/VisionCore/RecipeBundle.cs, dotnet/VisionCore/Jobs/Job.cs (bundle import/export, job name check only), dotnet/VisionStudio (Jobs.razor, Run.razor outside the Pareto card, HaystackMap.razor, HaystackScatterMap.razor, ToolingMatrix.razor, Services/Options.cs, appsettings.json, VisionStudio.csproj, i18n/en.json), VisionCore.Tests (RecipeBundleTests.cs, JobTests.cs bundle tests)
- Depends on: #267 (registers Req 17.11), #266 (this branch is stacked on it), the line-safety audit PR (claude/audit-tool-line-safety, edits Job.cs Validate)

## Section 5
- [x] (done 2026-11-03, PR #270: .vsrecipe bundles signed with the plant key and checked before anything is written; Run screen and labels show live values and factory names) Audit fixes Req 17.11 AUD-06 (recipe import: job name and path checked, plant-key signature via PackageIntegrity's SignedManifest, per-entry and total size caps while reading, golden image per job version, signed-in user logged), AUD-07 (Run screen: no made-up cycle time, yield, NG filmstrip or verdict text; "–" until there are values), AUD-09 labels (Process Stability Map, Seal Quality Check, Surface Texture Check, Anti-Glare Filter, Pouch Alignment through CamouflageLabels or plain factory words)

## Timeline (section 3)
| 2026-11-03 | .vsrecipe import refuses unsigned, re-signed, changed, added, over-size and path-escaping bundles before writing; golden image kept per job version; Run screen shows only live values; proprietary names off the Run, Teach and map screens. VisionCore.Tests 766 pass, VisionStudio.Tests 85 pass | 17.11 AUD-06, AUD-07, AUD-09 |

## Decisions (section 4)
| Recipe bundle signing | A .vsrecipe carries signature.json, the package-integrity SignedManifest (ECDSA P-256) over each entry's SHA-256, signed with the plant key; import needs Studio:RecipePublicKeyFile, export needs Studio:RecipeSigningKeyFile. No new crypto. The other PC's teach-image path and Locate pattern files are dropped on import, so an imported job with Locate is refused at load until taught here | 17.11 AUD-06, section 12 |

## Pending
- Bundles don't carry Locate pattern files: an imported job with a Locate tool is refused at load until its patterns are taught again on the new PC.
- The Run screen tabs SplitWipe, Tooling Matrix and Process Stability Map still draw sample images, zone temperatures and cluster counts; feed them live data or hide them until there is some.
