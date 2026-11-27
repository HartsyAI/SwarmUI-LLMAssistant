// Small PDF and Word files made in code, so no binary is kept in the repository.

/** A PDF with a page for each entry showing that line of text; an empty entry is a page with only a drawing on it, as a scan looks to a text reader. */
export function pdf(pages: string[]): Buffer {
    let objects = [
        '<< /Type /Catalog /Pages 2 0 R >>',
        `<< /Type /Pages /Kids [${pages.map((_, i) => `${4 + i * 2} 0 R`).join(' ')}] /Count ${pages.length} >>`,
        '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>',
    ];
    for (let [i, text] of pages.entries()) {
        let content = text ? `BT /F1 12 Tf 40 780 Td (${text.replace(/[\\()]/g, '\\$&')}) Tj ET` : '0 0 595 842 re f';
        objects.push(`<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents ${5 + i * 2} 0 R >>`);
        objects.push(`<< /Length ${content.length} >>\nstream\n${content}\nendstream`);
    }
    return assemble(objects);
}

/** A PDF that overflows the parser's stack while it is being opened: the page tree's only entry is an object that refers to itself. */
export function crashingPdf(): Buffer {
    return assemble(['<< /Type /Catalog /Pages 2 0 R >>', '<< /Type /Pages /Kids [3 0 R] /Count 1 >>', '3 0 R']);
}

/** A PDF file holding these objects, numbered from 1; the first is the catalog. */
function assemble(objects: string[]): Buffer {
    let file = '%PDF-1.4\n';
    let offsets = objects.map((body, i) => {
        let offset = file.length;
        file += `${i + 1} 0 obj\n${body}\nendobj\n`;
        return offset;
    });
    let table = file.length;
    file += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n${offsets.map(o => `${String(o).padStart(10, '0')} 00000 n \n`).join('')}`;
    file += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${table}\n%%EOF\n`;
    return Buffer.from(file, 'latin1');
}

/** A .docx with these paragraphs. */
export function docx(paragraphs: string[]): Buffer {
    let escape = (text: string) => text.replace(/&/g, '&amp;').replace(/</g, '&lt;');
    let body = paragraphs.map(p => `<w:p><w:r><w:t xml:space="preserve">${escape(p)}</w:t></w:r></w:p>`).join('');
    return zip({
        '[Content_Types].xml': '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>',
        'word/document.xml': `<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>${body}</w:body></w:document>`,
    });
}

/** A zip of these files, stored without compression. */
function zip(files: Record<string, string>): Buffer {
    let locals: Buffer[] = [];
    let central: Buffer[] = [];
    let offset = 0;
    for (let [name, text] of Object.entries(files)) {
        let data = Buffer.from(text, 'utf8');
        let label = Buffer.from(name, 'utf8');
        let crc = crc32(data);
        let local = Buffer.alloc(30);
        local.writeUInt32LE(0x04034b50, 0);
        local.writeUInt16LE(20, 4);
        local.writeUInt32LE(crc, 14);
        local.writeUInt32LE(data.length, 18);
        local.writeUInt32LE(data.length, 22);
        local.writeUInt16LE(label.length, 26);
        locals.push(local, label, data);
        let entry = Buffer.alloc(46);
        entry.writeUInt32LE(0x02014b50, 0);
        entry.writeUInt16LE(20, 4);
        entry.writeUInt16LE(20, 6);
        entry.writeUInt32LE(crc, 16);
        entry.writeUInt32LE(data.length, 20);
        entry.writeUInt32LE(data.length, 24);
        entry.writeUInt16LE(label.length, 28);
        entry.writeUInt32LE(offset, 42);
        central.push(entry, label);
        offset += 30 + label.length + data.length;
    }
    let directory = Buffer.concat(central);
    let end = Buffer.alloc(22);
    end.writeUInt32LE(0x06054b50, 0);
    end.writeUInt16LE(Object.keys(files).length, 8);
    end.writeUInt16LE(Object.keys(files).length, 10);
    end.writeUInt32LE(directory.length, 12);
    end.writeUInt32LE(offset, 16);
    return Buffer.concat([...locals, directory, end]);
}

/** The CRC-32 a zip records for each file. */
function crc32(data: Buffer): number {
    let crc = 0xffffffff;
    for (let byte of data) {
        crc ^= byte;
        for (let bit = 0; bit < 8; bit++) {
            crc = (crc >>> 1) ^ (0xedb88320 & -(crc & 1));
        }
    }
    return (crc ^ 0xffffffff) >>> 0;
}
