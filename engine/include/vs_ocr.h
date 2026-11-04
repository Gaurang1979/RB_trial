/*
 * vs_ocr.h - C API of the OCR / OCV tool (read text, check text).
 *
 * One handle = one text tool in a job (for example "batch, price and dates on the grey patch").
 * Create it with vs_ocr_default_params + vs_ocr_create, load the line recogniser once with
 * vs_ocr_load_model (or, for classical OCR, set reader = VS_READER_FONT and teach or load a font),
 * then call vs_ocr_run for every image. Print types: laser and TTO (solid
 * strokes) and CIJ (dot matrix, dots may be missing; text may be stretched or skewed).
 *
 * Two jobs in one call:
 *   Read text  (OCR): every line in the region is found, straightened and read.
 *   Check text (OCV): set the expected lines with vs_ocr_set_expected; each line then passes only
 *                     when every expected character is printed well enough (min_quality) and
 *                     nothing is printed beyond its ends. Learn good parts (vs_ocr_learn_print) to
 *                     also score each character's contrast, ink and position, and (allow_shift_px) to
 *                     limit where each line may sit. An expected line that is empty never passes.
 *
 * Expected-line patterns (UTF-8):
 *   plain characters      must be printed exactly, e.g. "\xE2\x82\xB9 0.23 per ml"
 *   ?                     any one character (full-text mode wildcard)
 *   {TIME:HH:mm+-N}       the time of the reference clock (vs_ocr_set_clock), within N minutes
 *   {DATE:fmt+Dd}         the reference date plus D days; fmt uses DD MM YY YYYY MMM, e.g.
 *                         {DATE:MM/YY+730d}; "+-N" after the days allows N days either way
 *   \?  \{  \\            a literal ?, { or backslash
 * Fixed fields (batch, MRP, Mfd and expiry dates entered at batch start) are filled in by the
 * caller before vs_ocr_set_expected.
 *
 * Read text with formats (tool specification 7.4): with no expected line set, vs_ocr_set_format gives a
 * format a line must fit. Each line is then read only with the characters its format allows at each
 * place, so an O is never read where only a digit may stand. The constraint never forces a pass: a
 * character keeps its own score (a printed O read as 0 scores low and fails), a mark the format has no
 * room for fails as VS_CHAR_EXTRA, and two equal characters with no gap seen between them score low.
 * Format syntax (UTF-8):
 *   plain characters      printed as is; letters match in either case
 *   a space               any spacing (spacing is never checked)
 *   {D:5} {D:4-6} {D}     digits: five, four to six, one or more; {A:..} letters, {X:..} letters or
 *                         digits, {?:..} any characters (counts up to 64)
 *   {|JAN|FEB|MAR}        one of these words (month names, day and month numbers)
 *   \{  \|  \\           a literal {, | or backslash
 * Under a format a character passes only at 50 or more, whatever min_quality (at most one character
 * can hold a place above 50). Formats are given to the found lines in their index order: each takes
 * the first unused line, top to bottom, whose free read already fits it (passing or not: a weak print
 * of the right line is never skipped for a later line) or that passes under it; when there is none,
 * the unused line it reads best (reported as failed); a format with no line left, or none reached in
 * the time budget, is reported as a line not found. Lines no format takes stay free reads.
 *
 * Threading: one handle must not be used from two threads at once. Strings returned by the getters
 * stay valid until the next vs_ocr_run or vs_ocr_destroy on the handle.
 */
#ifndef VS_OCR_H
#define VS_OCR_H

#include <stdint.h>
#include <stddef.h>

#ifdef _WIN32
#  ifdef VS_OCR_BUILD
#    define VS_OCR_API __declspec(dllexport)
#  else
#    define VS_OCR_API __declspec(dllimport)
#  endif
#else
#  define VS_OCR_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct vs_ocr vs_ocr;

/* Print type. AUTO tries solid first, then dot matrix for lines that read badly. */
enum vs_print {
    VS_PRINT_AUTO = 0,
    VS_PRINT_SOLID = 1,     /* laser, TTO, offset: continuous strokes */
    VS_PRINT_DOT = 2,       /* CIJ / dot matrix: dots are joined before reading (dot_size_px, join_strength) */
    VS_PRINT_INKJET = 3     /* TIJ / thermal inkjet: solid-looking strokes made of fine grain; the grain is
                               smoothed before reading. Never chosen by AUTO */
};

enum vs_polarity {
    VS_POLARITY_AUTO = 0,
    VS_POLARITY_DARK_ON_LIGHT = 1,
    VS_POLARITY_LIGHT_ON_DARK = 2  /* e.g. laser coding on a dark ink patch */
};

/* What reads the characters. FONT (classical OCR) needs no model: teach it with vs_ocr_teach_font from
   sample print, for fixed fonts, very fast lines or PCs without AI acceleration. */
enum vs_reader {
    VS_READER_AI = 0,       /* the line recogniser (vs_ocr_load_model) */
    VS_READER_FONT = 1      /* the taught font (vs_ocr_teach_font / vs_ocr_set_font) */
};

/* Character result of a checked line. */
enum vs_char_status {
    VS_CHAR_OK = 0,
    VS_CHAR_UNSURE = 1,    /* the expected character, but printed below min_quality */
    VS_CHAR_WRONG = 2,     /* another character is printed here */
    VS_CHAR_MISSING = 3,   /* nothing printed where the character should be */
    VS_CHAR_EXTRA = 4,     /* printed, but not in the expected text: free reads, and on a checked line a character
                              read beyond either end of the expected text (codepoint 0, found = what was read) */
    VS_CHAR_UNKNOWN = 5    /* taught font only, below min_quality: the expected character was never taught,
                              or what is printed looks like no taught character. Teach it (vs_ocr_teach_char) */
};

/* Why a checked character scores low (vs_ocr_char_quality.reason): the lowest of its scores. */
enum vs_print_reason {
    VS_PRINT_OK = 0,
    VS_PRINT_UNCLEAR = 1,  /* the recogniser is unsure it is this character */
    VS_PRINT_FAINT = 2,    /* less contrast than the good parts learned */
    VS_PRINT_BROKEN = 3,   /* less ink than the good parts learned: strokes or dots missing */
    VS_PRINT_SMEARED = 4,  /* more ink than the good parts learned: smear, blot, double strike */
    VS_PRINT_POSITION = 5  /* not where the good parts had it relative to the previous character */
};

/* Tool settings. Call vs_ocr_default_params first, then change what you need. */
typedef struct vs_ocr_params {
    int32_t struct_size;      /* sizeof(vs_ocr_params); set by vs_ocr_default_params */
    int32_t roi_x, roi_y, roi_w, roi_h; /* region with the text; roi_w = 0 means the whole image */
    int32_t print;            /* vs_print (default AUTO) */
    int32_t polarity;         /* vs_polarity (default AUTO) */
    int32_t char_height_px;   /* height of a capital letter in image pixels; 0 = estimate (slower) */
    int32_t max_lines;        /* most text lines kept, top to bottom (default 8) */
    float   min_quality;      /* 0..100: lowest character score that passes a check (default 60) */
    float   max_skew_deg;     /* rotation and slant searched, degrees either way (default 15) */
    int32_t time_budget_ms;   /* stop and report after this long (default 100) */
    int32_t reader;           /* vs_reader (default AI) */
    /* Added 2026-10-30 (tool spec 7.3, 7.5); callers built against the older struct get -1 from
       vs_ocr_set_params (struct_size), as for any size mismatch. */
    float   allow_shift_px;   /* Check text: each checked line must sit within this many image pixels of where
                                 the good parts had it (vs_ocr_learn_print; measured from the region's corner, so
                                 it moves with a fixture). 0 = not checked (default). Set and nothing learned yet:
                                 the line is NOT OK (vs_ocr_line_ext.shift_state) */
    float   dot_size_px;      /* dot print: diameter of one dot in image pixels (vs_ocr_measure_dots learns it);
                                 0 = from the character height (default) */
    float   join_strength;    /* dot print and inkjet: how strongly dots or grain are joined, 0.5..3 times the
                                 normal; 0 = normal (default). More joins dots with gaps (missing dots), less
                                 keeps close characters apart */
} vs_ocr_params;

/* One text line, top to bottom. */
typedef struct vs_ocr_line {
    const char* text;         /* free read, UTF-8 */
    const char* expected;     /* the expected text it was checked against (best candidate); "" = not checked */
    int32_t checked;          /* 1 = an expected line was set for it */
    int32_t pass;             /* checked lines: 1 = every expected character at or above min_quality; read lines
                                 (2026-10-31): 1 = quality at or above min_quality */
    float   quality;          /* checked: lowest expected-character score 0..100; read: lowest read confidence, at
                                 most the line confidence, below min_quality when the readers disagree (vs_agree) */
    int32_t first_char;       /* index of its first character in vs_ocr_get_char */
    int32_t char_count;
    float   x[4], y[4];       /* line corners in image pixels: top-left, top-right, bottom-right, bottom-left */
    float   angle_deg;        /* rotation found */
    float   slant_deg;        /* slant (shear) found */
    float   char_height_px;   /* measured capital height */
    int32_t format;           /* index of the format (vs_ocr_set_format) the line was read with; -1 = none */
} vs_ocr_line;

/* One character. Checked lines list the expected characters; read-only lines the read ones. */
typedef struct vs_ocr_char {
    int32_t codepoint;        /* Unicode code point (expected one for checked lines) */
    int32_t found;            /* code point read at this place; 0 = none */
    float   score;            /* 0..100 */
    int32_t status;           /* vs_char_status */
    float   x[4], y[4];       /* corners in image pixels */
} vs_ocr_char;

/* Print quality of one character of a checked line (vs_ocr_get_char_quality). Contrast, ink and pitch are
   measured on every run; they are scored only once good parts are learned (vs_ocr_learn_print): each
   scores 100 at the learned mean and min_quality at mean +- k sigma (k = 4), and vs_ocr_char.score is the
   lowest of recognition and these, so a character passes only when it is read and printed as well as
   the good parts. */
typedef struct vs_ocr_char_quality {
    int32_t struct_size;      /* sizeof(vs_ocr_char_quality); the caller sets it */
    float   recognition;      /* the recogniser's score 0..100 */
    float   contrast;         /* grey levels between ink and paper in the image */
    float   ink;              /* ink pixels in its cell of the line image (32 px high) */
    float   pitch;            /* line image pixels from the previous character's ink centre; 0 for the first */
    float   contrast_score;   /* 0..100 against the learned limits; 100 = no limit learned */
    float   ink_score;
    float   pitch_score;
    int32_t reason;           /* vs_print_reason; VS_PRINT_OK when score >= min_quality */
    int32_t learned;          /* 1 = at least one learned limit applied */
    /* Added 2026-10-31 (tool spec 7.3.3); callers built against the older struct (without it) still get the
       fields above. */
    float   margin;           /* verification margin in nats: ln(P(expected) / P(best rival)), the rival being any
                                 other character within a column of it or nothing printed there. Reported only;
                                 pass does not use it. A clear print has several nats; near 0 another character
                                 explains the image almost as well; below 0 the rival is more likely. 99 for a
                                 wildcard (?), -99 when the character could not be placed or its line was not
                                 found; 0 on lines read under a format */
} vs_ocr_char_quality;

/* Where a checked line sits against the learned position (allow_shift_px). */
enum vs_shift_state {
    VS_SHIFT_OFF = 0,         /* allow_shift_px = 0, or the line was not found */
    VS_SHIFT_OK = 1,          /* within allow_shift_px of the learned position */
    VS_SHIFT_BEYOND = 2,      /* farther: the line is NOT OK */
    VS_SHIFT_UNLEARNED = 3    /* no position learned for this line yet: the line is NOT OK (learn a good part:
                                 vs_ocr_learn_print accepts a run whose only fault is this) */
};

/* More about one line (vs_ocr_get_line_ext); the caller sets struct_size. New fields go at the end. */
typedef struct vs_ocr_line_ext {
    int32_t struct_size;      /* sizeof(vs_ocr_line_ext); the caller sets it */
    int32_t shift_state;      /* vs_shift_state */
    float   shift_px;         /* distance from the learned position, image pixels; -1 = unknown */
    float   shift_x, shift_y; /* that offset along x and y, image pixels */
    float   pos_x, pos_y;     /* checked lines: the first character's ink centre, image pixels from the region's corner */
    /* Added 2026-10-30; callers built against the older struct (without it) still get the fields above. */
    int32_t cut_short;        /* 1 = the text may go on past this line (letters in line just past a gap too wide to
                                 join, a letter on the region's edge, or ink just past an end): the line is scored
                                 below min_quality and fails, whatever it reads */
    /* Added 2026-10-31; callers built against an older struct (without it) still get the fields above. */
    float   margin;           /* checked lines: the lowest character margin (vs_ocr_char_quality.margin); how far
                                 the print is from being read as another text; -99 when the line was not found, 0 for lines
                                 not checked or read under a format */
    /* Added 2026-10-31 (Read text safety, vs_ocr_set_agreement); callers built against an older struct (without
       them) still get the fields above. */
    int32_t agreement;        /* vs_agree_state */
    int32_t second_readers;   /* the readers that read the line again: bits 1 << vs_second_reader; 0 = none */
    float   line_confidence;  /* Read text: how sure the reader is of the whole line, 0..100 (calibrated); the line's
                                 quality is at most this. 100 for the taught font and for checked lines */
} vs_ocr_line_ext;

/* Read text (no expected line set): a second reader must read the same text, or the line is NOT OK (study
   04c 7.1). A free read or a read under a format then passes only when both read the same characters (spaces
   are not compared) and each at or above min_quality; the line's quality is the lower of the two. A line
   already below min_quality is not read again. Check text is not affected. */
enum vs_agree {
    VS_AGREE_OFF = 0,         /* the first reader alone decides: only for text that decides nothing */
    VS_AGREE_AUTO = 1,        /* default: every independent second reader there is (the second model, and the taught
                                 font when the AI reads first or the AI model when the font reads first); with none,
                                 the same reader on the line moved by half a step (VS_SECOND_SHIFT) */
    VS_AGREE_SHIFT = 2        /* only the same reader on the line moved by half a step */
};

/* Which reader read a line again (vs_ocr_line_ext.second_readers, as bits 1 << value). */
enum vs_second_reader {
    VS_SECOND_MODEL = 0,      /* the second AI model (vs_ocr_load_second_model) */
    VS_SECOND_FONT = 1,       /* the taught font, the AI reading first */
    VS_SECOND_AI = 2,         /* the AI model, the taught font reading first */
    VS_SECOND_SHIFT = 3       /* the first reader on the line moved by half a step: catches reads that hang on the
                                 exact cut, not a reader's blind spot (it is not independent) */
};

enum vs_agree_state {
    VS_AGREE_NOT_CHECKED = 0, /* Check text, agreement off, or the line already below min_quality */
    VS_AGREE_SAME = 1,        /* every second reader read the same text */
    VS_AGREE_DIFFERENT = 2    /* a second reader read other text, or none could read it in the time budget: the line
                                 is scored below min_quality and fails; the characters that differ are UNSURE */
};

typedef struct vs_ocr_stats {
    int32_t lines;            /* lines found */
    int32_t expected_lines;   /* lines set with vs_ocr_set_expected */
    int32_t pass;             /* 1 = every expected line (or format) found and passed (or neither set: >= 1 line read,
                                 and every line read passed, since 2026-10-31) */
    float   locate_ms;
    float   read_ms;
    float   total_ms;
    int32_t timed_out;
} vs_ocr_stats;

VS_OCR_API void        vs_ocr_default_params(vs_ocr_params* p);
VS_OCR_API vs_ocr*     vs_ocr_create(const vs_ocr_params* p);
VS_OCR_API void        vs_ocr_destroy(vs_ocr* t);
VS_OCR_API int32_t     vs_ocr_set_params(vs_ocr* t, const vs_ocr_params* p);

/* The line recogniser (ocrnet.onnx) and its character list (one character per line, UTF-8, index 1 first;
   index 0 is the CTC blank). Returns 0 on success. */
VS_OCR_API int32_t     vs_ocr_load_model(vs_ocr* t, const char* onnx_path, const char* charset_path);

/* Load the line recogniser from the decrypted ONNX bytes already in memory, so the plaintext model
   never has to exist as a file (AES-256-GCM at rest, content key from the licence; LICENSING.md 7.4).
   `onnx` holds `onnx_size` bytes of a complete ONNX model; the charset is still a file path (it is not
   a secret). Returns 0, or < 0 when the buffer is empty or not a loadable model (vs_ocr_last_error);
   on failure no model is loaded, so the tool stays NG rather than reading without the recogniser. */
VS_OCR_API int32_t     vs_ocr_load_model_buffer(vs_ocr* t, const uint8_t* onnx, size_t onnx_size, const char* charset_path);
VS_OCR_API int32_t     vs_ocr_load_second_model_buffer(vs_ocr* t, const uint8_t* onnx, size_t onnx_size, const char* charset_path);

/* Expected text for line `line` (0 = top). pattern = NULL clears it. Clear all with vs_ocr_clear_expected. */
/* Read text safety (see vs_agree): the mode (default VS_AGREE_AUTO; returns 0, or -1 for an unknown mode), and a
   second AI model of another kind, trained apart from the first, read as a second reader. onnx_path = NULL
   unloads it. Returns 0, or < 0 when it cannot be loaded (vs_ocr_last_error). */
VS_OCR_API int32_t     vs_ocr_set_agreement(vs_ocr* t, int32_t mode);
VS_OCR_API int32_t     vs_ocr_load_second_model(vs_ocr* t, const char* onnx_path, const char* charset_path);

VS_OCR_API int32_t     vs_ocr_set_expected(vs_ocr* t, int32_t line, const char* pattern);
VS_OCR_API void        vs_ocr_clear_expected(vs_ocr* t);
/* Reference clock for {TIME} and {DATE}: Unix seconds (formatted in the PC's local time zone); 0 = the PC
   clock at each run. Set it when checking stored images, to the time each was taken. */
VS_OCR_API void        vs_ocr_set_clock(vs_ocr* t, int64_t unix_seconds);

/* Read text mode formats (see the top of this file), used only while no expected line is set. index
   0..63; format = NULL clears it. Returns 0, or < 0 when the format does not compile (vs_ocr_last_error).
   The free read of a line with a format is replaced by its read under the format; checked = 1, expected
   = the format, pass and quality as for a checked line, characters as read under the format. */
VS_OCR_API int32_t     vs_ocr_set_format(vs_ocr* t, int32_t index, const char* format);
VS_OCR_API void        vs_ocr_clear_formats(vs_ocr* t);

/* Text-line layout (tool study 7.4.6): the block's lines top to bottom, each as its groups of characters
   apart by word gaps, lines apart by "|". "2 2 2-4" is one line of three groups (2, 2 and 2 to 4
   characters); "6 | 4 2" two lines. Characters count as printed (punctuation too, spaces not). It is used
   to find the lines: pieces of one line that a gap wider than a word gap split are joined when more lines
   are found than the layout lists. Read text mode: the layout's lines are matched to the found lines in
   order; each matched line is read with as many characters as its layout line allows (checked = 1,
   expected = that count as a format, as under vs_ocr_set_format) and must fit it: its count, and each
   group's when every word gap is seen (fewer gaps seen: the count only; more: fails). A format set for
   the line is read as usual and must fit too. Check text mode: expected line i is layout line i; its
   expected text must have a count the layout allows, and at least as many lines as the layout lists must
   be found. A layout line not found, or a line that does not fit, fails the part (stats.pass = 0) with
   the reason in vs_ocr_layout_reason; lines read under it but failing it score below min_quality.
   NULL or "" clears it. Returns 0, or < 0 when it does not parse (vs_ocr_last_error). vs_ocr_get_layout
   copies the layout as normalised text (returns the bytes needed with the NUL). */
VS_OCR_API int32_t     vs_ocr_set_layout(vs_ocr* t, const char* layout);
VS_OCR_API int32_t     vs_ocr_get_layout(const vs_ocr* t, char* buf, int32_t cap);
/* Teach: sets the layout from the last run, which must have passed (a good part): its lines top to bottom,
   each grouped as printed (checked lines by their expected text, grouped by the gaps the reader saw when it
   read as many characters; read lines as read). Returns the lines learned, or < 0. */
VS_OCR_API int32_t     vs_ocr_learn_layout(vs_ocr* t);
/* Why the last run's lines do not fit the layout, e.g. "layout line 2 (4 2): 5 characters read (5), the
   layout 4 2 has 6"; "" when they fit or no layout is set. */
VS_OCR_API const char* vs_ocr_layout_reason(const vs_ocr* t);
/* The layout line line `index` (as vs_ocr_get_line) was matched to; -1 = none. */
VS_OCR_API int32_t     vs_ocr_line_layout(const vs_ocr* t, int32_t index);
/* Dot print spaced tight (tool study 7.5.1): the gap between two characters is no wider than a gap
   between the dots of one character, so joining dots joins characters. On (1), dots are joined along the
   line only within about half a dot (across it as before: a missing dot is still bridged), the taught
   font's pitch is found on dots not joined along the line, and dot print is melted less along it. Off (0,
   the default) for print with a dot column or more between characters. */
VS_OCR_API void        vs_ocr_set_tight_spacing(vs_ocr* t, int32_t tight);
VS_OCR_API int32_t     vs_ocr_get_tight_spacing(const vs_ocr* t);

/* Classical OCR font (reader = VS_READER_FONT). vs_ocr_teach_font finds the lines in the image with the
   handle's settings and teaches the characters of line `line` (0 = top) from `text`, what is printed
   there (UTF-8, spaces ignored); call it on several prints to teach more characters or variations.
   Returns the characters taught, or < 0 when the line is not found or does not cut into that many
   characters (vs_ocr_last_error says why). The font is kept with the job as text: vs_ocr_get_font
   copies it into buf (NUL-terminated) when cap is large enough and returns the bytes needed with the
   NUL; vs_ocr_set_font loads one (0 on success). */
VS_OCR_API int32_t     vs_ocr_teach_font(vs_ocr* t, const uint8_t* pixels, int32_t width, int32_t height, int32_t stride,
                                         int32_t line, const char* text);
/* "Teach this character": adds the one character printed at image point (x, y), for example the centre of a
   vs_ocr_char box from a run that failed, to the font as `character` (one UTF-8 character), without teaching
   the whole line again. The font must already have characters. Returns 1, or < 0 when there is no character
   there or it clearly looks like another taught character (vs_ocr_last_error says why). */
VS_OCR_API int32_t     vs_ocr_teach_char(vs_ocr* t, const uint8_t* pixels, int32_t width, int32_t height, int32_t stride,
                                         float x, float y, const char* character);
VS_OCR_API void        vs_ocr_clear_font(vs_ocr* t);
VS_OCR_API int32_t     vs_ocr_font_glyphs(const vs_ocr* t);   /* distinct characters taught */
VS_OCR_API int32_t     vs_ocr_get_font(const vs_ocr* t, char* buf, int32_t cap);
VS_OCR_API int32_t     vs_ocr_set_font(vs_ocr* t, const char* font);

/* Print quality learned from good parts (Check text). After a run that passed, vs_ocr_learn_print adds that
   part's characters to the reference and returns the good parts learned (< 0 when the last run did not
   pass or checked nothing). Limits apply from 10 good parts (tool spec: 10-30). The reference is kept with
   the job as text, like the font: vs_ocr_get_print copies it (returns the bytes needed with the NUL),
   vs_ocr_set_print loads one (0 on success; a damaged one is refused and the old one kept). */
VS_OCR_API int32_t     vs_ocr_learn_print(vs_ocr* t);
VS_OCR_API void        vs_ocr_clear_print(vs_ocr* t);
VS_OCR_API int32_t     vs_ocr_print_parts(const vs_ocr* t);
VS_OCR_API int32_t     vs_ocr_get_print(const vs_ocr* t, char* buf, int32_t cap);
VS_OCR_API int32_t     vs_ocr_set_print(vs_ocr* t, const char* ref);

/* Dot print (tool spec 7.5): measures the dots in the handle's region of one image, for dot_size_px. Returns
   the median dot diameter in image pixels and, when pitch_px is given, the median distance between
   neighbouring dots; < 0 when the region holds no dot print (solid strokes, too few dots: vs_ocr_last_error). */
VS_OCR_API float       vs_ocr_measure_dots(vs_ocr* t, const uint8_t* pixels, int32_t width, int32_t height, int32_t stride,
                                           float* pitch_px);

/* Read (and check) the text in one 8-bit grey image. Returns 0 when it ran (see stats.pass), < 0 on error. */
VS_OCR_API int32_t     vs_ocr_run(vs_ocr* t, const uint8_t* pixels, int32_t width, int32_t height, int32_t stride);

VS_OCR_API int32_t     vs_ocr_line_count(const vs_ocr* t);
VS_OCR_API int32_t     vs_ocr_get_line(const vs_ocr* t, int32_t index, vs_ocr_line* out);
VS_OCR_API int32_t     vs_ocr_char_count(const vs_ocr* t);
VS_OCR_API int32_t     vs_ocr_get_char(const vs_ocr* t, int32_t index, vs_ocr_char* out);
/* Print quality of character `index` (as vs_ocr_get_char); zeros for characters of free reads and extras.
   struct_size may also be the size before margin was added (then margin is not set). */
VS_OCR_API int32_t     vs_ocr_get_char_quality(const vs_ocr* t, int32_t index, vs_ocr_char_quality* out);
VS_OCR_API void        vs_ocr_get_stats(const vs_ocr* t, vs_ocr_stats* out);
/* Shift state, position, cut short and margin of line `index` (as vs_ocr_get_line); -1 when struct_size is not
   sizeof(vs_ocr_line_ext) or the size of an older version of it (before margin, or before cut_short: then only
   the fields that version holds are set). */
VS_OCR_API int32_t     vs_ocr_get_line_ext(const vs_ocr* t, int32_t index, vs_ocr_line_ext* out);

/* The straightened line images of the last run (height 32, text dark on light), for training data
   and the teach screen. Copies up to cap bytes; returns the needed size (w * 32), or < 0. */
VS_OCR_API int32_t     vs_ocr_get_line_image(const vs_ocr* t, int32_t index, uint8_t* buf, int32_t cap, int32_t* width);

VS_OCR_API const char* vs_ocr_last_error(const vs_ocr* t);
VS_OCR_API const char* vs_ocr_version(void);

#ifdef __cplusplus
}
#endif

#endif
