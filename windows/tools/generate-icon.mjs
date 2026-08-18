#!/usr/bin/env node
/**
 * Erzeugt assets/app.ico für AT Panther – 1:1 das Icon der Android-Version:
 *
 *   - Hintergrund:  Vollfläche #1565C0 (ic_launcher_background)
 *   - Vordergrund:  ic_launcher_foreground.xml als 48-dp-Vektor, zentriert im
 *                   108-dp-Foreground-Layer (Scale 48/108 ≈ 44,4 %):
 *                    - Pantherpfoten-Silhouette #1565C0
 *                    - drei Pfotenballen #BBDEFB
 *
 * Die Pfad-Daten werden direkt aus den Android-XML-Dateien übernommen,
 * als Bézier-Kurven abgetastet und per Polygon-Rasterisierung gezeichnet
 * (3x3-Supersampling für glatte Kanten). Reines Node, keine Abhängigkeiten.
 *
 * Aufruf:  bun tools/generate-icon.mjs
 */
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = join(dirname(fileURLToPath(import.meta.url)), "..", "AT.Panther");

// ── Android-Icon-Quelldaten (aus app/src/main/res) ───────────────────────────

const COLOR_BG = [0x15, 0x65, 0xC0];      // ic_launcher_background #1565C0
const COLOR_PAW = [0x15, 0x65, 0xC0];     // Pfoten-Silhouette (wie Hintergrund)
const COLOR_PADS = [0xBB, 0xDE, 0xFB];    // Pfotenballen #BBDEFB

/** viewBox 48×48; Reihenfolge = Zeichenreihenfolge (Pfote zuerst, dann Ballen). */
const PATHS = [
  // Panther paw outline
  {
    fill: COLOR_PAW,
    d: "M24,38 C24,38 16,36 14,30 C12,24 14,18 18,16 C20,15 22,16 23,18 L24,20 L25,18 C26,16 28,15 30,16 C34,18 36,24 34,30 C32,36 24,38 24,38Z",
  },
  // Left pad
  {
    fill: COLOR_PADS,
    d: "M18,22 C18,20.5 19.5,19 21,19 C22.5,19 23,20.5 22.5,22 C22,23.5 20,24 19,23 C18.5,22.5 18,23 18,22Z",
  },
  // Right pad
  {
    fill: COLOR_PADS,
    d: "M30,22 C30,20.5 28.5,19 27,19 C25.5,19 25,20.5 25.5,22 C26,23.5 28,24 29,23 C29.5,22.5 30,23 30,22Z",
  },
  // Center pad
  {
    fill: COLOR_PADS,
    d: "M22,27 C22,25.5 23,24 24.5,24 C26,24 27,25.5 26.5,27 C26,28.5 24,29 23,28.5 C22.5,28 22,28 22,27Z",
  },
];

// ── Pfad-Parser + Abtastung ──────────────────────────────────────────────────

/** Zerlegt eine SVG-Path-Data in Polygone (Bézier-Abtastung). */
function pathToPolygons(d, steps = 20) {
  const polygons = [];
  let current = [];
  let cursor = [0, 0];
  let start = [0, 0];

  // Robuster Tokenizer: Kommandos und Zahlen getrennt sammeln (verkettete
  // Zahlen wie „…18,22Z“ dürfen nicht abgeschnitten werden).
  const segments = [];
  let pending = null;
  const re = /[MCLZ]|-?\d+(?:\.\d+)?/g;
  let m;
  while ((m = re.exec(d))) {
    if (m[0] === "M" || m[0] === "C" || m[0] === "L" || m[0] === "Z") {
      pending = { cmd: m[0], nums: [] };
      segments.push(pending);
    } else {
      pending?.nums.push(Number(m[0]));
    }
  }

  for (const { cmd, nums } of segments) {
    switch (cmd) {
      case "M": {
        if (current.length) polygons.push(current);
        current = [];
        cursor = [nums[0], nums[1]];
        start = cursor;
        current.push(cursor);
        break;
      }
      case "L": {
        cursor = [nums[0], nums[1]];
        current.push(cursor);
        break;
      }
      case "C": {
        // Mehrere Kurven pro Kommando unterstützen (6er-Blöcke)
        for (let k = 0; k + 5 < nums.length; k += 6) {
          const p1 = [nums[k], nums[k + 1]];
          const p2 = [nums[k + 2], nums[k + 3]];
          const p3 = [nums[k + 4], nums[k + 5]];
          for (let i = 1; i <= steps; i++) {
            const t = i / steps;
            const mt = 1 - t;
            const x = mt ** 3 * cursor[0] + 3 * mt ** 2 * t * p1[0] + 3 * mt * t ** 2 * p2[0] + t ** 3 * p3[0];
            const y = mt ** 3 * cursor[1] + 3 * mt ** 2 * t * p1[1] + 3 * mt * t ** 2 * p2[1] + t ** 3 * p3[1];
            current.push([x, y]);
          }
          cursor = p3;
        }
        break;
      }
      case "Z": {
        if (cursor[0] !== start[0] || cursor[1] !== start[1]) current.push(start);
        if (current.length >= 3) polygons.push(current);
        current = [];
        break;
      }
    }
  }
  if (current.length >= 3) polygons.push(current);
  return polygons;
}

/** Ray-Casting-Punkt-im-Polygon-Test. */
function pointInPolygon(px, py, polygon) {
  let inside = false;
  for (let i = 0, j = polygon.length - 1; i < polygon.length; j = i++) {
    const [xi, yi] = polygon[i];
    const [xj, yj] = polygon[j];
    if ((yi > py) !== (yj > py) && px < ((xj - xi) * (py - yi)) / (yj - yi) + xi) {
      inside = !inside;
    }
  }
  return inside;
}

function sameColor(a, b) {
  return a[0] === b[0] && a[1] === b[1] && a[2] === b[2];
}

// ── Rendering ────────────────────────────────────────────────────────────────

/**
 * Zeichnet das Android-Icon in RGBA (Größe x Größe).
 * Foreground-Layer: 48-dp-Pfote zentriert im 108-dp-Canvas →
 * Pfot-Box = [30/108, 78/108] = [0.2778, 0.7222] (normalisiert).
 */
function drawIcon(size) {
  const px = new Uint8Array(size * size * 4);
  const scale = size / 108;          // 48 dp im 108-dp-Layer
  const offset = size * 0.2778;      // (108 − 48) / 2 / 108

  const rendered = PATHS.map((p) => ({
    fill: p.fill,
    polygons: pathToPolygons(p.d),
  }));

  const toCanvas = (vx, vy) => [offset + vx * scale, offset + vy * scale];

  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      let r = 0, g = 0, b = 0;
      // 3x3-Supersampling
      for (let sy = 0; sy < 3; sy++) {
        for (let sx = 0; sx < 3; sx++) {
          const pxm = x + (sx + 0.5) / 3;
          const pym = y + (sy + 0.5) / 3;
          let color = COLOR_BG; // Standard: Hintergrund
          for (const shape of rendered) {
            for (const poly of shape.polygons) {
              const canvasPoly = poly.map(([vx, vy]) => toCanvas(vx, vy));
              if (pointInPolygon(pxm, pym, canvasPoly)) {
                color = shape.fill;
                break;
              }
            }
            // Wertvergleich (nicht Referenzvergleich!): früher Exit, sobald
            // ein sichtbares Shape getroffen wurde – die Pfoten-Silhouette
            // hat dieselbe Farbe wie der Hintergrund und zählt nicht.
            if (!sameColor(color, COLOR_BG)) break;
          }
          r += color[0]; g += color[1]; b += color[2];
        }
      }
      const idx = (y * size + x) * 4;
      px[idx] = Math.round(r / 9);
      px[idx + 1] = Math.round(g / 9);
      px[idx + 2] = Math.round(b / 9);
      px[idx + 3] = 255;
    }
  }
  return px;
}

// ── ICO-Encoding (32-bpp BMP in ICO) ─────────────────────────────────────────

/** RGBA-Pixel → BMP (BITMAPINFOHEADER + XOR + AND-Maske) für ein ICO. */
function bmpFromPixels(px, size) {
  const rowBytes = Math.ceil(size / 32) * 4;
  const xorSize = size * size * 4;
  const andSize = rowBytes * size;
  const bmp = Buffer.alloc(40 + xorSize + andSize);

  bmp.writeUInt32LE(40, 0);
  bmp.writeInt32LE(size, 4);
  bmp.writeInt32LE(size * 2, 8);
  bmp.writeUInt16LE(1, 12);
  bmp.writeUInt16LE(32, 14);
  bmp.writeUInt32LE(0, 16);
  bmp.writeUInt32LE(xorSize + andSize, 20);

  // XOR: Bottom-up-Zeilen, BGRA-Reihenfolge
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      const src = ((size - 1 - y) * size + x) * 4;
      const dst = 40 + (y * size + x) * 4;
      bmp[dst] = px[src + 2];
      bmp[dst + 1] = px[src + 1];
      bmp[dst + 2] = px[src];
      bmp[dst + 3] = px[src + 3];
    }
  }
  return bmp;
}

function makeIco(sizes) {
  const header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(sizes.length, 4);

  const entries = [];
  const bmps = [];
  let offset = 6 + 16 * sizes.length;
  for (const { size, px } of sizes) {
    const bmp = bmpFromPixels(px, size);
    const entry = Buffer.alloc(16);
    entry.writeUInt8(size >= 256 ? 0 : size, 0);
    entry.writeUInt8(size >= 256 ? 0 : size, 1);
    entry.writeUInt8(0, 2);
    entry.writeUInt8(0, 3);
    entry.writeUInt16LE(1, 4);
    entry.writeUInt16LE(32, 6);
    entry.writeUInt32LE(bmp.length, 8);
    entry.writeUInt32LE(offset, 12);
    entries.push(entry);
    bmps.push(bmp);
    offset += bmp.length;
  }
  return Buffer.concat([header, ...entries, ...bmps]);
}

// ── Ausgabe ──────────────────────────────────────────────────────────────────

const sizes = [16, 24, 32, 48, 64, 128, 256].map((size) => ({ size, px: drawIcon(size) }));
const ico = makeIco(sizes);

const outPath = join(ROOT, "assets", "app.ico");
mkdirSync(dirname(outPath), { recursive: true });
writeFileSync(outPath, ico);
console.log(`app.ico geschrieben: ${outPath} (${ico.length} Bytes, ${sizes.map((s) => s.size).join("/")} px)`);
