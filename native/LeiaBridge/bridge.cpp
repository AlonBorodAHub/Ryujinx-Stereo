#include <windows.h>
#include <gl/GL.h>
#include <sr/management/srcontext.h>
#include <sr/weaver/glweaver.h>
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <memory>
#include <stdexcept>
#include <string>

namespace {
using Fence = void*;
using FenceFn = Fence(APIENTRY*)(GLenum, GLbitfield);
using WaitFn = void(APIENTRY*)(Fence, GLbitfield, std::uint64_t);
using DeleteFn = void(APIENTRY*)(Fence);
using BindFramebufferFn = void(APIENTRY*)(GLenum, GLuint);
thread_local std::string error;
constexpr GLenum Framebuffer = 0x8D40, FramebufferSrgb = 0x8DB9;
constexpr GLenum GpuCommandsComplete = 0x9117;
constexpr std::uint64_t TimeoutIgnored = ~std::uint64_t(0);

template<class T> T proc(const char* name) {
    auto value = wglGetProcAddress(name);
    if (!value || reinterpret_cast<std::intptr_t>(value) <= 3 || value == reinterpret_cast<PROC>(-1))
        throw std::runtime_error(std::string("Missing GL entry point: ") + name);
    return reinterpret_cast<T>(value);
}
struct Current {
    HDC dc = wglGetCurrentDC();
    HGLRC context = wglGetCurrentContext();
    Current(HDC target, HGLRC next) {
        if (!wglMakeCurrent(target, next)) throw std::runtime_error("Cannot bind isolated weaver context");
    }
    ~Current() { wglMakeCurrent(dc, context); }
};
struct Bridge {
    HDC dc{};
    HGLRC context{};
    SR::SRContext* sr{};
    SR::IGLWeaver1* weaver{};
    FenceFn fence{};
    WaitFn wait{};
    DeleteFn deleteFence{};
    BindFramebufferFn bindFramebuffer{};
    GLuint input{};
    int width{}, height{};
    ~Bridge() {
        if (!context) return;
        try {
            Current current(dc, context);
            if (weaver) { weaver->destroy(); weaver = nullptr; }
            if (sr) { SR::SRContext::deleteSRContext(sr); sr = nullptr; }
        } catch (...) { }
        wglDeleteContext(context);
    }
};
void failure(const char* text) { error = text ? text : "Unknown native panel output error"; }
}

extern "C" __declspec(dllexport) const char* __cdecl zl_error() { return error.c_str(); }

extern "C" __declspec(dllexport) void* __cdecl zl_create(int activatePanel) {
    try {
        error.clear();
        auto bridge = std::make_unique<Bridge>();
        bridge->dc = wglGetCurrentDC();
        HGLRC source = wglGetCurrentContext();
        if (!source || !bridge->dc) throw std::runtime_error("No current source OpenGL context");
        bridge->context = wglCreateContext(bridge->dc);
        if (!bridge->context || !wglShareLists(source, bridge->context))
            throw std::runtime_error("Cannot create shared isolated weaver context");
        {
            Current current(bridge->dc, bridge->context);
            bridge->fence = proc<FenceFn>("glFenceSync");
            bridge->wait = proc<WaitFn>("glWaitSync");
            bridge->deleteFence = proc<DeleteFn>("glDeleteSync");
            bridge->bindFramebuffer = proc<BindFramebufferFn>("glBindFramebuffer");
            if (activatePanel) {
                bridge->sr = SR::SRContext::create(true);
                if (!bridge->sr) throw std::runtime_error("LeiaSR context creation failed");
                auto result = SR::CreateGLWeaver(*bridge->sr, WindowFromDC(bridge->dc), &bridge->weaver);
                if (result != WeaverSuccess || !bridge->weaver)
                    throw std::runtime_error("LeiaSR OpenGL weaver creation failed: " + std::to_string(result));
                bridge->weaver->setShaderSRGBConversion(true, true);
                bridge->weaver->setLatencyInFrames(1);
                bridge->sr->initialize();
            }
        }
        return bridge.release();
    } catch (const std::exception& ex) { failure(ex.what()); }
    catch (...) { failure("Unexpected exception creating native panel output"); }
    return nullptr;
}

// The two fences order GPU work across contexts without CPU waits. Keeping all
// weaver GL state in its own context avoids invalidating emulator state caches.
extern "C" __declspec(dllexport) int __cdecl zl_weave(void* handle, GLuint texture, int width, int height, Fence ready) {
    auto bridge = static_cast<Bridge*>(handle);
    try {
        if (!bridge || !bridge->weaver) throw std::runtime_error("Native panel output not initialized");
        Fence done{};
        {
            Current current(bridge->dc, bridge->context);
            bridge->wait(ready, 0, TimeoutIgnored);
            bridge->bindFramebuffer(Framebuffer, 0);
            glViewport(0, 0, width, height);
            glDisable(GL_SCISSOR_TEST);
            glDisable(GL_DEPTH_TEST);
            glDisable(GL_BLEND);
            glDisable(FramebufferSrgb);
            glColorMask(GL_TRUE, GL_TRUE, GL_TRUE, GL_TRUE);
            if (bridge->input != texture || bridge->width != width || bridge->height != height) {
                bridge->weaver->setInputViewTexture(texture, width / 2, height, 0x8058 /* GL_RGBA8 */);
                bridge->input = texture; bridge->width = width; bridge->height = height;
            }
            bridge->weaver->weave();
            done = bridge->fence(GpuCommandsComplete, 0);
            glFlush();
        }
        bridge->wait(done, 0, TimeoutIgnored);
        bridge->deleteFence(done);
        return 1;
    } catch (const std::exception& ex) { failure(ex.what()); }
    catch (...) { failure("Unexpected exception weaving native panel frame"); }
    return 0;
}

// Fixture-only read of a tiny shared texture. This does not create an SRContext
// or activate the display's lenses and is never used in the gameplay path.
extern "C" __declspec(dllexport) int __cdecl zl_probe(void* handle, GLuint texture, unsigned char* rgba, int size, Fence ready) {
    try {
        auto bridge = static_cast<Bridge*>(handle);
        if (!bridge || size != 16) throw std::runtime_error("Probe requires a 2x2 RGBA texture");
        Current current(bridge->dc, bridge->context);
        bridge->wait(ready, 0, TimeoutIgnored);
        glBindTexture(GL_TEXTURE_2D, texture);
        glGetTexImage(GL_TEXTURE_2D, 0, GL_RGBA, GL_UNSIGNED_BYTE, rgba);
        if (glGetError() != GL_NO_ERROR) throw std::runtime_error("Shared-context probe GL error");
        return 1;
    } catch (const std::exception& ex) { failure(ex.what()); }
    catch (...) { failure("Shared-context probe failed"); }
    return 0;
}

extern "C" __declspec(dllexport) void __cdecl zl_destroy(void* handle) {
    delete static_cast<Bridge*>(handle);
}
