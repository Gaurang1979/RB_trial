# OCR Read text / Check text: Indian month names, MRP fields and Legal Metrology date rules (study 04c, wave 1 text)

- Status: done
- Session: indian-text-formats, 2026-10-31
- Pull request: #161
- Section 5 item: new
- Components/files: dotnet/VisionCore (Tools/TextFormat.cs, Tools/TextTool.cs, Tools/AutoFind.cs), dotnet/VisionCore.Tests (IndianTextFormatTests.cs new), dotnet/VisionStudio/i18n/en.json, docs/vision-system/TOOL-SPECIFICATIONS.md; no engine change
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #161: month-name language en-IN (JUNE, JULY, SEPT, full names); Read text fields {MRP}, {PRICE}, {MRP:whole}, {MRP:46.00}, {PER} and {LIFE}, read by the engine under the format; a zero price fails; a printed shelf life counts the expiry from MFD, must not be past and must agree with EXP and the expiry months; teaching proposes them and names dates from the Indian labels) Indian month names, MRP and unit sale price, and best-before shelf life in the Read text formats (study 04c 7.4.1, 7.4.3, 7.4.4)

## Timeline (section 3)
| 2026-10-31 | Read text formats for Indian packs: Indian English month names, MRP and unit sale price fields (₹, Rs., INR; exact amount; Indian grouping), best-before shelf life with date rules; the engine reads them under the format | 7.4, 12 |

## Decisions (section 4)
| Price fields have fixed shapes | The engine reads a line only in the shapes its format allows and cannot skip an optional part, so {MRP} is rupees and paise, {MRP:whole} whole rupees and {MRP:46.00} an exact amount; the word lists (labels, currencies, units) are the same for the check and the engine, so a print one accepts the other never refuses | 7.4, 12 |
| Shelf life counts from MFD | "Best before 18 months from MFG" needs the MFD date on the pack (setup refuses a {LIFE} without one); the expiry it gives is MFD + N months less a day (to the end of the month for a month-only MFD), the earlier end the expiry rule accepts, so a pack is never passed on the later reading | 7.4, 12 |
| Hindi month names wait | The AI reader's charset (ml/models/ocr_charset.txt) has no Devanagari, so Hindi month names would be refused at save on every job; they come with the Devanagari reader | 7.4, 7.10 |

## Update log (section 8)
| 2026-10-31 | TOOL-SPECIFICATIONS.md 7.4: Indian packs paragraph (en-IN months, MRP / PRICE / PER / LIFE fields, explain.format.price_invalid) | Claude (Mac, branch claude/indian-text-formats) |

## Pending
- Hindi (Devanagari) month names and label words (निर्माण तिथि, समाप्ति तिथि) for MMM dates, once the AI reader reads Devanagari (study 04c 7.4.1, 7.8).
- The Vision Studio strings explain.format.price_invalid and the new text.formats_hint wording in the other 14 languages.
