#include "Bridging.h"

#include <math.h>
#include <onnxruntime/coreml_provider_factory.h>
#include <onnxruntime/onnxruntime_c_api.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// The same model and numbers as BirdTrigger.cs on a computer.
static const int FirstAnimal = 16, LastAnimal = 25; // the COCO numbers the model uses, bird through giraffe
static const float Mean[3] = {0.485f, 0.456f, 0.406f};
static const float Std[3] = {0.229f, 0.224f, 0.225f};

static const OrtApi *g_ort;
static OrtEnv *g_env;
static OrtMemoryInfo *g_memory;
static OrtSession *g_session;
static char *g_input;
static float *g_pixels;

#define TRY(call) do { OrtStatus *status_ = (call); if (status_) { note(status_, error, cap); goto fail; } } while (0)

static void note(OrtStatus *status, char *error, int cap)
{
    if (error && cap > 0) snprintf(error, cap, "%s", g_ort->GetErrorMessage(status));
    g_ort->ReleaseStatus(status);
}

static OrtStatus *add_core_ml(OrtSessionOptions *options, const char *cache)
{
    // The Neural Engine rather than the graphics chip, which costs less battery for a model this size.
    const char *keys[] = {kCoremlProviderOption_ModelFormat, kCoremlProviderOption_MLComputeUnits,
                          kCoremlProviderOption_ModelCacheDirectory};
    const char *values[] = {"MLProgram", "CPUAndNeuralEngine", cache};
    return g_ort->SessionOptionsAppendExecutionProvider(options, "CoreML", keys, values, cache ? 3 : 2);
}

int animal_open(const char *model, const char *cache, int core_ml, char *error, int cap)
{
    OrtSessionOptions *options = NULL;
    OrtAllocator *allocator = NULL;
    if (error && cap > 0) error[0] = 0;
    if (g_session) return 0;
    if (!g_ort) {
        // ONNX Runtime reports its use to Microsoft unless told not to, as on a computer.
        setenv("ORT_DISABLE_TELEMETRY", "1", 1);
        g_ort = OrtGetApiBase()->GetApi(ORT_API_VERSION);
        if (!g_ort) {
            if (error && cap > 0) snprintf(error, cap, "this ONNX Runtime is too old");
            return -1;
        }
    }
    if (!g_env) {
        TRY(g_ort->CreateEnv(ORT_LOGGING_LEVEL_ERROR, "CamRemote", &g_env));
        TRY(g_ort->DisableTelemetryEvents(g_env));
        TRY(g_ort->CreateCpuMemoryInfo(OrtArenaAllocator, OrtMemTypeDefault, &g_memory));
    }
    TRY(g_ort->CreateSessionOptions(&options));
    TRY(g_ort->SetIntraOpNumThreads(options, 2));
    TRY(g_ort->SetInterOpNumThreads(options, 1));
    TRY(g_ort->SetSessionGraphOptimizationLevel(options, ORT_ENABLE_ALL));
    // The model takes any size, and Core ML can only take over the parts whose shapes are known.
    TRY(g_ort->AddFreeDimensionOverrideByName(options, "batch_size", 1));
    TRY(g_ort->AddFreeDimensionOverrideByName(options, "height", ANIMAL_SIZE));
    TRY(g_ort->AddFreeDimensionOverrideByName(options, "width", ANIMAL_SIZE));
    if (core_ml) TRY(add_core_ml(options, cache));
    TRY(g_ort->CreateSession(g_env, model, options, &g_session));
    TRY(g_ort->GetAllocatorWithDefaultOptions(&allocator));
    char *name = NULL;
    TRY(g_ort->SessionGetInputName(g_session, 0, allocator, &name));
    g_input = strdup(name);
    allocator->Free(allocator, name);
    g_pixels = malloc(sizeof(float) * 3 * ANIMAL_SIZE * ANIMAL_SIZE);
    g_ort->ReleaseSessionOptions(options);
    return 0;

fail:
    if (options) g_ort->ReleaseSessionOptions(options);
    animal_close();
    return -1;
}

// A float tensor of 1 by rows by columns.
static int output(OrtValue *value, const float **data, int64_t *rows, int64_t *columns)
{
    OrtTensorTypeAndShapeInfo *info = NULL;
    size_t dims = 0;
    int64_t shape[3] = {0};
    ONNXTensorElementDataType type = 0;
    int ok = !g_ort->GetTensorTypeAndShape(value, &info)
             && !g_ort->GetTensorElementType(info, &type) && type == ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT
             && !g_ort->GetDimensionsCount(info, &dims) && dims == 3
             && !g_ort->GetDimensions(info, shape, 3)
             && !g_ort->GetTensorMutableData(value, (void **)data);
    if (info) g_ort->ReleaseTensorTypeAndShapeInfo(info);
    *rows = shape[1];
    *columns = shape[2];
    return ok ? 0 : -1;
}

int animal_look(const uint8_t *rgba, int rotation, float *found)
{
    if (!g_session) return -1;
    // Shown upright, turned by rotation degrees clockwise, because the model finds far more animals that way.
    const int size = ANIMAL_SIZE, last = size - 1, plane = size * size;
    for (int y = 0; y < size; y++) {
        for (int x = 0; x < size; x++) {
            int fx = x, fy = y;
            if (rotation == 90) { fx = y; fy = last - x; }
            else if (rotation == 180) { fx = last - x; fy = last - y; }
            else if (rotation == 270) { fx = last - y; fy = x; }
            const uint8_t *p = rgba + (fy * size + fx) * 4;
            for (int c = 0; c < 3; c++) g_pixels[c * plane + y * size + x] = (p[c] / 255.0f - Mean[c]) / Std[c];
        }
    }

    const int64_t shape[] = {1, 3, size, size};
    const char *inputs[] = {g_input}, *outputs[] = {"logits", "pred_boxes"};
    OrtValue *tensor = NULL, *results[2] = {NULL, NULL};
    int rc = -1;
    if (g_ort->CreateTensorWithDataAsOrtValue(g_memory, g_pixels, sizeof(float) * 3 * plane, shape, 4,
                                              ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT, &tensor)) goto done;
    OrtStatus *status = g_ort->Run(g_session, NULL, inputs, (const OrtValue *const *)&tensor, 1, outputs, 2, results);
    if (status) {
        g_ort->ReleaseStatus(status);
        goto done;
    }
    const float *logits, *boxes;
    int64_t queries, classes, box_count, box_size;
    if (output(results[0], &logits, &queries, &classes) || output(results[1], &boxes, &box_count, &box_size)
        || queries != box_count || box_size != 4 || classes <= LastAnimal) goto done;

    float best = -INFINITY;
    int64_t at = 0;
    for (int64_t i = 0; i < queries; i++) {
        for (int animal = FirstAnimal; animal <= LastAnimal; animal++) {
            float v = logits[i * classes + animal];
            if (v > best) {
                best = v;
                at = i;
            }
        }
    }
    float bx = boxes[at * 4], by = boxes[at * 4 + 1], bw = boxes[at * 4 + 2], bh = boxes[at * 4 + 3];
    // Back to the camera's own way up.
    float cx = bx, cy = by;
    if (rotation == 90) { cx = by; cy = 1 - bx; }
    else if (rotation == 180) { cx = 1 - bx; cy = 1 - by; }
    else if (rotation == 270) { cx = 1 - by; cy = bx; }
    int across = rotation == 90 || rotation == 270;
    found[0] = 1 / (1 + expf(-best));
    found[1] = cx;
    found[2] = cy;
    found[3] = across ? bh : bw;
    found[4] = across ? bw : bh;
    rc = 0;

done:
    if (tensor) g_ort->ReleaseValue(tensor);
    for (int i = 0; i < 2; i++)
        if (results[i]) g_ort->ReleaseValue(results[i]);
    return rc;
}

void animal_close(void)
{
    if (!g_ort) return;
    if (g_session) g_ort->ReleaseSession(g_session);
    g_session = NULL;
    free(g_input);
    g_input = NULL;
    free(g_pixels);
    g_pixels = NULL;
}
