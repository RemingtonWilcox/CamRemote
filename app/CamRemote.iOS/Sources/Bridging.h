#ifndef Bridging_h
#define Bridging_h

#include <stdint.h>

// Channel 0 carries commands, channel 1 carries the camera's events. Call everything from one thread.
int cam_reachable(const char *host, int timeout_ms);
int cam_find(const char *prefix, char *out, int cap);

#define CAM_UNREACHABLE (-1)
#define CAM_KEY_CHANGED (-2)
#define CAM_BAD_LOGIN (-3)
#define CAM_FAILED (-4)

// `expected` is the key seen last time, or empty the first time. `found` gets the key seen now.
int cam_ssh_open(const char *host, const char *user, const char *password, const char *expected, char *found, int found_cap);
int cam_ssh_read(int channel, void *buffer, int size, int timeout_ms);
int cam_ssh_read_some(int channel, void *buffer, int size);
int cam_ssh_write(int channel, const void *buffer, int size);
void cam_ssh_close(void);

// `bins` holds 256 brightness counts. `overlay` is RGBA the same size as the picture, and is filled only for zebra or peaking.
void cam_guides(const uint8_t *rgba, int w, int h, int zebra, int peaking, int level, int *bins, uint8_t *overlay);

#endif
