#ifndef Bridging_h
#define Bridging_h

#include <stdint.h>

// Channel 0 carries commands, channel 1 carries the camera's events. Call everything from one thread.
int cam_reachable(const char *host, int timeout_ms);
int cam_find(const char *prefix, char *out, int cap);

#define CAM_UNREACHABLE (-1)
#define CAM_BAD_LOGIN (-3)
#define CAM_FAILED (-4)

// Opening is two steps: `found` gets the camera's key, which says which saved login to send, then the login.
int cam_ssh_hello(const char *host, char *found, int found_cap);
int cam_ssh_login(const char *user, const char *password);
int cam_ssh_read(int channel, void *buffer, int size, int timeout_ms);
int cam_ssh_read_some(int channel, void *buffer, int size);
int cam_ssh_write(int channel, const void *buffer, int size);
void cam_ssh_close(void);

// `bins` holds 256 brightness counts. `overlay` is RGBA the same size as the picture, and is filled only for zebra or peaking.
void cam_guides(const uint8_t *rgba, int w, int h, int zebra, int peaking, int level, int *bins, uint8_t *overlay);

// The animal detector: RF-DETR Small through ONNX Runtime, on the phone only. Call these one at a time.
#define ANIMAL_SIZE 512
// `cache` is where Core ML keeps the model it builds, or NULL. `core_ml` 0 runs it on the processor alone.
int animal_open(const char *model, const char *cache, int core_ml, char *error, int cap);
// `rgba` is ANIMAL_SIZE square, the camera's way up. `first_class` to `last_class` are the COCO numbers to look for: 16 to 25 for
// animals, 16 for birds, 1 for people. `found` gets the best one's score from 0 to 1, then its box as 0..1 of the
// picture: centre x, centre y, width, height.
int animal_look(const uint8_t *rgba, int rotation, int first_class, int last_class, float *found);
void animal_close(void);

#endif
