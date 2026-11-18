#include <SDL.h>
#include <fcntl.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

static int fail(const char* reason) {
    fprintf(stderr, "%s: %s\n", reason, SDL_GetError());
    SDL_Quit();
    return 1;
}

int main(int argc, char** argv) {
    const SDL_version* version = SDL_Linked_Version();
    if (version->major != 1 || version->minor != 0 || version->patch != 8)
        return fail("Expected SDL 1.0.8");

    if (SDL_Init(SDL_INIT_VIDEO) < 0)
        return fail("SDL_Init");

    char driver[16];
    if (SDL_VideoDriverName(driver, sizeof driver) == NULL || strcmp(driver, "fbcon") != 0)
        return fail("Expected fbcon");

    SDL_Surface* screen = SDL_SetVideoMode(320, 240, 16, SDL_SWSURFACE);
    if (screen == NULL)
        return fail("SDL_SetVideoMode");

    Uint32 color = SDL_MapRGB(screen->format, 0x31, 0x69, 0xab);
    if (SDL_FillRect(screen, NULL, color) < 0 || SDL_Flip(screen) < 0)
        return fail("SDL framebuffer update");

    int framebuffer = open("/dev/fb0", O_RDONLY);
    Uint16 first_pixel = 0;
    if (framebuffer < 0 || read(framebuffer, &first_pixel, sizeof first_pixel) != sizeof first_pixel ||
        first_pixel != color)
        return fail("Framebuffer readback did not match the SDL frame");
    close(framebuffer);
    puts("SDL 1.0.8 fbcon frame verified");
    fflush(stdout);

    if (argc > 1 && strcmp(argv[1], "--input") == 0) {
        puts("Press A and click inside the framebuffer window");
        fflush(stdout);
        int saw_key = 0;
        int saw_click = 0;
        Uint32 start = SDL_GetTicks();
        while ((!saw_key || !saw_click) && SDL_GetTicks() - start < 20000) {
            SDL_Event event;
            while (SDL_PollEvent(&event)) {
                if (event.type == SDL_KEYDOWN && event.key.keysym.sym == SDLK_a && !saw_key) {
                    puts("SDL 1.0.8 fbcon keyboard verified");
                    fflush(stdout);
                    saw_key = 1;
                }
                if (event.type == SDL_MOUSEBUTTONDOWN && event.button.button == 1 && !saw_click) {
                    puts("SDL 1.0.8 fbcon pointer verified");
                    fflush(stdout);
                    saw_click = 1;
                }
            }
            SDL_Delay(10);
        }
        if (!saw_key || !saw_click)
            return fail("Timed out waiting for SDL keyboard and pointer events");
    }

    SDL_Quit();
    return 0;
}
