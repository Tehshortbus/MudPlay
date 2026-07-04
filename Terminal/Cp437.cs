namespace FujinTerm.Terminal;

// Lookup table that maps each of the 256 IBM PC code-page-437 byte values to
// its modern Unicode equivalent.
//
// CP437 is the original IBM PC character set: ASCII in the 0x20–0x7E range
// plus a rich set of box-drawing, block, and accented characters in the upper
// half. BBSes and ANSI art rely on these specific glyphs, so raw 8-bit bytes
// from the wire need to be translated through this table before they can be
// rendered with a Unicode-aware font.
public static class Cp437
{
    // Index into this table = the raw byte value received.
    // Value at that index   = the Unicode character to render.
    // The 0x00–0x1F range, normally ASCII control codes, is repurposed by
    // CP437 for symbols (smileys, suits, arrows, etc.) — that's why those
    // entries aren't blank.
    private static readonly char[] Table =
    {
        ' ','☺','☻','♥','♦','♣','♠','•',
        '◘','○','◙','♂','♀','♪','♫','☼',
        '►','◄','↕','‼','¶','§','▬','↨',
        '↑','↓','→','←','∟','↔','▲','▼',
        ' ','!','"','#','$','%','&','\'','(',')','*','+',',','-','.','/',
        '0','1','2','3','4','5','6','7','8','9',':',';','<','=','>','?',
        '@','A','B','C','D','E','F','G','H','I','J','K','L','M','N','O',
        'P','Q','R','S','T','U','V','W','X','Y','Z','[','\\',']','^','_',
        '`','a','b','c','d','e','f','g','h','i','j','k','l','m','n','o',
        'p','q','r','s','t','u','v','w','x','y','z','{','|','}','~','⌂',
        'Ç','ü','é','â','ä','à','å','ç',
        'ê','ë','è','ï','î','ì','Ä','Å',
        'É','æ','Æ','ô','ö','ò','û','ù',
        'ÿ','Ö','Ü','¢','£','¥','₧','ƒ',
        'á','í','ó','ú','ñ','Ñ','ª','º',
        '¿','⌐','¬','½','¼','¡','«','»',
        '░','▒','▓','│','┤','╡','╢','╖',
        '╕','╣','║','╗','╝','╜','╛','┐',
        '└','┴','┬','├','─','┼','╞','╟',
        '╚','╔','╩','╦','╠','═','╬','╧',
        '╨','╤','╥','╙','╘','╒','╓','╫',
        '╪','┘','┌','█','▄','▌','▐','▀',
        'α','ß','Γ','π','Σ','σ','µ','τ',
        'Φ','Θ','Ω','δ','∞','φ','ε','∩',
        '≡','±','≥','≤','⌠','⌡','÷','≈',
        '°','∙','·','√','ⁿ','²','■',' ',
    };

    // Return the Unicode glyph that corresponds to a CP437 byte.
    public static char ToUnicode(byte b) => Table[b];
}
