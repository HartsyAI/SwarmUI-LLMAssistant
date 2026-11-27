import zlib from 'node:zlib';

/** The spread between the darkest and the brightest byte of the pixel data of an 8-bit, non-interlaced RGB or RGBA PNG, over a sample
 * of the colour channels. Null for any other file. A picture of one flat colour (a black frame) has a spread near 0. */
export function pngSpread(file: Buffer): number | null {
    if (file.length < 33 || file.readUInt32BE(0) !== 0x89504e47) {
        return null;
    }
    let width = file.readUInt32BE(16);
    let height = file.readUInt32BE(20);
    let [depth, colour, interlace] = [file[24], file[25], file[28]];
    if (depth !== 8 || (colour !== 2 && colour !== 6) || interlace !== 0) {
        return null;
    }
    let parts: Buffer[] = [];
    for (let at = 8; at + 8 <= file.length; at += file.readUInt32BE(at) + 12) {
        if (file.toString('latin1', at + 4, at + 8) === 'IDAT') {
            parts.push(file.subarray(at + 8, at + 8 + file.readUInt32BE(at)));
        }
    }
    let raw = zlib.inflateSync(Buffer.concat(parts));
    let channels = colour === 6 ? 4 : 3;
    let stride = width * channels;
    let previous = Buffer.alloc(stride);
    let [low, high] = [255, 0];
    for (let y = 0; y < height; y++) {
        let filter = raw[y * (stride + 1)];
        let line = Buffer.from(raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1)));
        for (let i = 0; i < stride; i++) {
            let left = i >= channels ? line[i - channels] : 0;
            let up = previous[i];
            let corner = i >= channels ? previous[i - channels] : 0;
            let add = 0;
            if (filter === 1) {
                add = left;
            }
            else if (filter === 2) {
                add = up;
            }
            else if (filter === 3) {
                add = (left + up) >> 1;
            }
            else if (filter === 4) {
                let guess = left + up - corner;
                let [a, b, c] = [Math.abs(guess - left), Math.abs(guess - up), Math.abs(guess - corner)];
                add = a <= b && a <= c ? left : b <= c ? up : corner;
            }
            line[i] = (line[i] + add) & 255;
            // Every seventh pixel, colour channels only.
            if (i % channels < 3 && Math.floor(i / channels) % 7 === 0) {
                low = Math.min(low, line[i]);
                high = Math.max(high, line[i]);
            }
        }
        previous = line;
    }
    return high - low;
}
