# -*- coding: utf-8 -*-
"""Rasterise the icon SVGs with headless Edge and pack them into a multi-size .ico.

No third-party packages: the ICO container is a 6-byte header, a 16-byte directory entry per
image, and the payloads. Windows has accepted PNG-compressed entries since Vista and this app
targets 10.0.19041, so every entry is a PNG rather than the old BMP-with-AND-mask form.
"""
import io, os, struct, subprocess, sys, time

SP = os.path.dirname(os.path.abspath(__file__))
EDGE = r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"

# Where the cutover goes was measured, not assumed. The first guess put it at 48, on the theory
# that dropping detail helps once the detail is sub-pixel. The renders said otherwise: at 24 and 32
# the mushy keys are the only thing that makes the form a mouse, and without them the solid body
# reads as an egg. Only at 16 and 20 does the master lose - there it speckles into noise and the
# clean reduction wins. A seam-only middle variant was tried for 16-32 and rejected: at 16 it is a
# smudge, and at 24 the cut reads as a chip out of the edge rather than a panel line.
PLAN = [(16, 'small'), (20, 'small'),
        (24, 'master'), (32, 'master'), (48, 'master'),
        (64, 'master'), (128, 'master'), (256, 'master')]
SRC = {'master': 'naga-icon.svg', 'small': 'naga-icon-small.svg'}


def shoot(svg, size, out):
    """One PNG at exactly size x size, transparent outside the coin."""
    page = os.path.join(SP, '_shot.html')
    io.open(page, 'w', encoding='utf-8').write(
        '<!doctype html><meta charset="utf-8">'
        '<style>html,body{margin:0;padding:0;background:transparent;overflow:hidden}'
        'img{display:block}</style>'
        '<img src="%s" width="%d" height="%d">' % (svg, size, size))
    if os.path.exists(out):
        os.remove(out)
    # --force-device-scale-factor is deliberately absent: with it, Edge writes no file at window
    # sizes this small. --default-background-color keeps the area outside the coin transparent;
    # without it every entry ships on a white square.
    subprocess.run([EDGE, '--headless', '--disable-gpu', '--hide-scrollbars',
                    '--default-background-color=00000000',
                    '--screenshot=' + out, '--window-size=%d,%d' % (size, size),
                    'file:///' + page.replace(os.sep, '/')],
                   capture_output=True)
    for _ in range(40):                      # the write lands a moment after the process exits
        if os.path.exists(out) and os.path.getsize(out) > 0:
            break
        time.sleep(0.1)
    else:
        print('FAILED to render ' + out)
        sys.exit(1)
    return io.open(out, 'rb').read()


def has_alpha(png):
    """PNG colour type lives at byte 25; 6 = truecolour with alpha, 4 = grey with alpha."""
    return png[25] in (4, 6)


def main():
    out_ico = sys.argv[1] if len(sys.argv) > 1 else os.path.join(SP, 'app.ico')
    images = []
    for size, which in PLAN:
        png = shoot(SRC[which], size, os.path.join(SP, '_ico-%d.png' % size))
        print('  %3d px  %-7s %6d bytes  alpha=%s' % (size, which, len(png), has_alpha(png)))
        if not has_alpha(png):
            print('  refusing to pack an opaque entry: the coin would ship on a white square')
            sys.exit(1)
        images.append((size, png))

    offset = 6 + 16 * len(images)
    header = struct.pack('<HHH', 0, 1, len(images))
    directory, payload = b'', b''
    for size, png in images:
        b = 0 if size == 256 else size          # 0 means 256 in an ICONDIRENTRY
        directory += struct.pack('<BBBBHHII', b, b, 0, 0, 1, 32, len(png), offset)
        payload += png
        offset += len(png)

    io.open(out_ico, 'wb').write(header + directory + payload)
    print('wrote %s (%d entries, %d bytes)' % (out_ico, len(images),
                                               os.path.getsize(out_ico)))


if __name__ == '__main__':
    main()
