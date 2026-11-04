# .vsrecipe bundles carry taught Locate patterns and reference pictures, signed and checked before anything is written

- Status: done
- Session: recipe-bundle-taught-images, 2026-11-03
- Pull request: #305
- Section 5 item: new, Req 17.11 AUD-12 (Pending of 2026-11-03-claude-audit-recipe-run-labels and 2026-11-03-claude-audit-tool-line-safety)
- Components/files: dotnet/VisionCore/RecipeBundle.cs, dotnet/VisionCore/Jobs/Job.cs (ExportBundle, ImportBundle only), dotnet/CodeReader/BmpImage.cs (encode to bytes), dotnet/VisionStudio/Components/Pages/Jobs.razor (one help line), VisionCore.Tests (RecipeBundleTests, JobTests bundle tests), docs/REQUIREMENTS-VISION-SYSTEM.md (17.11 AUD-06 item)
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #305: pattern-<tool>.bmp and reference-<tool>.bmp entries inside the plant-key signature; checked before anything is written; older bundles still read) .vsrecipe bundles carry taught Locate patterns and reference pictures (Req 17.11 AUD-12)

## Timeline (section 3)
| 2026-11-03 | A .vsrecipe carries each taught Find the part pattern and reference picture, signed with the rest; an import checks them (signed, readable, for a taught tool of that kind, reference the size of its area) before writing, and refuses a bundle missing a picture an enabled tool needs. A job with Locate or taught references now moves between PCs without reteaching | 17.11 AUD-06, AUD-12 |

## Decisions (section 4)
| Taught pictures in a bundle | One flat entry per tool, named by its place in tools.json (`pattern-3.bmp`, `reference-5.bmp`, no leading zeros, `\z`-anchored), as 8-bit BMP encoded from the pixels that loaded, not copied from disk. Same SignedManifest, 16 MB per picture, 256 MB total unchanged; the entry count cap goes from 16 to 64 to leave room. A missing picture refuses the bundle only for an enabled tool; a disabled tool imports untaught. A picture for no taught tool of its kind is refused (tools.json and pictures do not belong together). The other PC's ReferenceFile is now cleared like its PatternFile | 17.11 AUD-12, section 12 |

## Update log (section 8)
| 2026-11-03 | Requirements 17.11: AUD-12 added (taught pictures in the bundle) | Claude (Mac, branch claude/recipe-bundle-taught-images) |
