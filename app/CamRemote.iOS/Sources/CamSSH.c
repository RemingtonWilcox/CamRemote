#include "Bridging.h"

#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <libssh2.h>
#include <netinet/in.h>
#include <poll.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>

static const int RemotePort = 64321; // only Sony's cameras answer here, which tells them from other devices
static const int CommandPort = 15740;

static int g_socket = -1;
static LIBSSH2_SESSION *g_session;
static LIBSSH2_CHANNEL *g_channels[2];
static const char *g_password;

static int connect_to(const char *host, int port, int timeout_ms)
{
    struct sockaddr_in address = {0};
    address.sin_len = sizeof address;
    address.sin_family = AF_INET;
    address.sin_port = htons(port);
    if (inet_pton(AF_INET, host, &address.sin_addr) != 1) return -1;

    int fd = socket(AF_INET, SOCK_STREAM, 0);
    if (fd < 0) return -1;
    int on = 1;
    setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &on, sizeof on); // a dropped link must be an error, not a crash
    int flags = fcntl(fd, F_GETFL, 0);
    fcntl(fd, F_SETFL, flags | O_NONBLOCK);
    if (connect(fd, (struct sockaddr *)&address, sizeof address) != 0) {
        struct pollfd p = {fd, POLLOUT, 0};
        int failure = 0;
        socklen_t size = sizeof failure;
        if (errno != EINPROGRESS || poll(&p, 1, timeout_ms) <= 0 ||
            getsockopt(fd, SOL_SOCKET, SO_ERROR, &failure, &size) != 0 || failure != 0) {
            close(fd);
            return -1;
        }
    }
    fcntl(fd, F_SETFL, flags);
    return fd;
}

int cam_reachable(const char *host, int timeout_ms)
{
    int fd = connect_to(host, RemotePort, timeout_ms);
    if (fd < 0) return 0;
    close(fd);
    return 1;
}

int cam_find(const char *prefix, char *out, int cap)
{
    for (int last = 2; last < 15; last++) {
        char host[32];
        snprintf(host, sizeof host, "%s%d", prefix, last);
        if (cam_reachable(host, 350)) {
            snprintf(out, cap, "%s", host);
            return 0;
        }
    }
    return -1;
}

static void answer_prompts(const char *name, int name_len, const char *instruction, int instruction_len,
                           int prompts, const LIBSSH2_USERAUTH_KBDINT_PROMPT *asked,
                           LIBSSH2_USERAUTH_KBDINT_RESPONSE *answers, void **abstract)
{
    for (int i = 0; i < prompts; i++) {
        answers[i].text = strdup(g_password);
        answers[i].length = (unsigned int)strlen(g_password);
    }
}

static int give_up(int code)
{
    cam_ssh_close();
    return code;
}

int cam_ssh_hello(const char *host, char *found, int found_cap)
{
    g_socket = connect_to(host, 22, 5000);
    if (g_socket < 0) return CAM_UNREACHABLE;
    libssh2_init(0);
    g_session = libssh2_session_init();
    if (!g_session) return give_up(CAM_FAILED);
    libssh2_session_set_timeout(g_session, 15000);
    if (libssh2_session_handshake(g_session, g_socket) != 0) return give_up(CAM_FAILED);

    // The camera's own key says which camera this is, before any password is sent.
    const unsigned char *key = (const unsigned char *)libssh2_hostkey_hash(g_session, LIBSSH2_HOSTKEY_HASH_SHA256);
    if (!key || found_cap < 65) return give_up(CAM_FAILED);
    for (int i = 0; i < 32; i++) snprintf(found + i * 2, 3, "%02x", key[i]);
    return 0;
}

int cam_ssh_login(const char *user, const char *password)
{
    g_password = password;
    int login = libssh2_userauth_keyboard_interactive(g_session, user, answer_prompts);
    g_password = NULL;
    if (login != 0) return give_up(CAM_BAD_LOGIN);

    for (int i = 0; i < 2; i++) {
        g_channels[i] = libssh2_channel_direct_tcpip_ex(g_session, "localhost", CommandPort, "127.0.0.1", 0);
        if (!g_channels[i]) return give_up(CAM_FAILED);
    }
    libssh2_session_set_blocking(g_session, 0);
    return 0;
}

static void wait_for_socket(int timeout_ms)
{
    int directions = libssh2_session_block_directions(g_session);
    struct pollfd p = {g_socket, 0, 0};
    if (directions & LIBSSH2_SESSION_BLOCK_INBOUND) p.events |= POLLIN;
    if (directions & LIBSSH2_SESSION_BLOCK_OUTBOUND) p.events |= POLLOUT;
    poll(&p, 1, timeout_ms);
}

int cam_ssh_read(int channel, void *buffer, int size, int timeout_ms)
{
    int waited = 0; // counted, not read off the clock, so time paused by iOS doesn't count as the camera being silent
    int got = 0;
    while (got < size) {
        ssize_t n = libssh2_channel_read(g_channels[channel], (char *)buffer + got, size - got);
        if (n > 0) {
            got += (int)n;
            waited = 0;
        } else if (n == LIBSSH2_ERROR_EAGAIN || n == 0) {
            if (n == 0 && libssh2_channel_eof(g_channels[channel])) return -1;
            if (waited > timeout_ms) return -1;
            wait_for_socket(100);
            waited += 100;
        } else {
            return -1;
        }
    }
    return got;
}

int cam_ssh_read_some(int channel, void *buffer, int size)
{
    ssize_t n = libssh2_channel_read(g_channels[channel], buffer, size);
    return n == LIBSSH2_ERROR_EAGAIN ? 0 : (int)n;
}

int cam_ssh_write(int channel, const void *buffer, int size)
{
    int waited = 0;
    int sent = 0;
    while (sent < size) {
        ssize_t n = libssh2_channel_write(g_channels[channel], (const char *)buffer + sent, size - sent);
        if (n >= 0) {
            sent += (int)n;
        } else if (n == LIBSSH2_ERROR_EAGAIN) {
            if (waited > 15000) return -1;
            wait_for_socket(100);
            waited += 100;
        } else {
            return -1;
        }
    }
    return sent;
}

void cam_ssh_close(void)
{
    if (g_session) libssh2_session_set_blocking(g_session, 1);
    for (int i = 0; i < 2; i++) {
        if (g_channels[i]) libssh2_channel_free(g_channels[i]);
        g_channels[i] = NULL;
    }
    if (g_session) {
        libssh2_session_disconnect(g_session, "bye");
        libssh2_session_free(g_session);
        g_session = NULL;
    }
    if (g_socket >= 0) close(g_socket);
    g_socket = -1;
}
