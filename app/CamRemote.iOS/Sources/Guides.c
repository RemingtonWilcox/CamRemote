#include "Bridging.h"

#include <stdlib.h>
#include <string.h>

// The same marks as phone.html, so a phone connected either way shows the same thing.
static const int Reach = 3;
static const int MinRun = 24;
static const int MinContrast = 30;
static const int MinStep = 12; // 40% of MinContrast

// Marks edges by how crisp they are, not how strong, so a soft edge against bright sky stays clear.
// Only connected runs are kept: sky through distant leaves also looks like crisp dots, but real focus shows up as lines.
static void mark_peaks(const uint8_t *l, int w, int h, uint8_t *marks)
{
    int n = w * h;
    uint8_t *crisp = calloc(n, 1);
    int *todo = malloc(n * sizeof(int));
    int *run = malloc(n * sizeof(int));

    for (int y = Reach; y < h - Reach - 1; y++) {
        for (int x = Reach; x < w - Reach - 1; x++) {
            int i = y * w + x;
            int across = abs(l[i + 1] - l[i]), down = abs(l[i + w] - l[i]);
            int step = across > down ? across : down;
            if (step < MinStep) continue;
            int stride = across >= down ? 1 : w, low = 255, high = 0;
            for (int k = -Reach; k <= Reach + 1; k++) {
                int v = l[i + k * stride];
                if (v < low) low = v;
                if (v > high) high = v;
            }
            int contrast = high - low;
            if (contrast >= MinContrast && step * 5 >= contrast * 2) crisp[i] = 1;
        }
    }

    for (int start = 0; start < n; start++) {
        if (crisp[start] != 1) continue;
        int size = 0, waiting = 0, left = w, right = 0, top = h, bottom = 0;
        crisp[start] = 2;
        todo[waiting++] = start;
        while (waiting) {
            int i = todo[--waiting], x = i % w, y = i / w;
            run[size++] = i;
            if (x < left) left = x;
            if (x > right) right = x;
            if (y < top) top = y;
            if (y > bottom) bottom = y;
            for (int dy = -1; dy <= 1; dy++) {
                for (int dx = -1; dx <= 1; dx++) {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int next = ny * w + nx;
                    if (crisp[next] == 1) {
                        crisp[next] = 2;
                        todo[waiting++] = next;
                    }
                }
            }
        }
        if ((right - left > bottom - top ? right - left : bottom - top) < MinRun) continue;
        for (int k = 0; k < size; k++) {
            int i = run[k];
            marks[i] = marks[i + 1] = marks[i + w] = marks[i + w + 1] = 1;
        }
    }
    free(crisp);
    free(todo);
    free(run);
}

void cam_guides(const uint8_t *rgba, int w, int h, int zebra, int peaking, int level, int *bins, uint8_t *overlay)
{
    int n = w * h;
    uint8_t *luma = malloc(n);
    memset(bins, 0, 256 * sizeof(int));
    for (int i = 0; i < n; i++) {
        luma[i] = (uint8_t)((rgba[i * 4] * 54 + rgba[i * 4 + 1] * 183 + rgba[i * 4 + 2] * 19) >> 8);
        bins[luma[i]]++;
    }
    if (zebra || peaking) {
        memset(overlay, 0, n * 4);
        if (zebra) {
            int period = w / 110 > 10 ? w / 110 : 10, half = period / 2;
            for (int y = 0; y < h; y++) {
                for (int x = 0; x < w; x++) {
                    if (luma[y * w + x] >= level && (x + y) % period < half) overlay[(y * w + x) * 4 + 3] = 199;
                }
            }
        }
        if (peaking) {
            uint8_t *marks = calloc(n + w + 2, 1);
            mark_peaks(luma, w, h, marks);
            for (int i = 0; i < n; i++) {
                if (marks[i]) {
                    overlay[i * 4] = 255;
                    overlay[i * 4 + 1] = 48;
                    overlay[i * 4 + 2] = 48;
                    overlay[i * 4 + 3] = 255;
                }
            }
            free(marks);
        }
    }
    free(luma);
}
