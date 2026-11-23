package com.phone2win.mic;

public final class Protocol {
    public static final int DEFAULT_PORT = 54321;
    public static final int HEADER_SIZE = 16;
    public static final byte VERSION = 1;

    public static final byte PKT_AUDIO = 1;
    public static final byte PKT_HEARTBEAT = 2;
    public static final byte PKT_DISCOVER_REQ = 3;
    public static final byte PKT_DISCOVER_RESP = 4;
    public static final byte PKT_HEARTBEAT_ACK = 5;
    public static final byte PKT_STOP = 6;

    private Protocol() {
    }

    public static void writeHeader(
            byte[] buf,
            byte pktType,
            int seq,
            int sampleRate,
            byte channels,
            byte bitsPerSample) {
        buf[0] = (byte) 'P';
        buf[1] = (byte) '2';
        buf[2] = (byte) 'W';
        buf[3] = (byte) 'M';
        buf[4] = VERSION;
        buf[5] = pktType;
        buf[6] = channels;
        buf[7] = bitsPerSample;
        writeUInt32LE(buf, 8, seq);
        writeUInt32LE(buf, 12, sampleRate);
    }

    public static byte[] buildControlPacket(byte pktType, int seq, int sampleRate) {
        byte[] buf = new byte[HEADER_SIZE];
        writeHeader(buf, pktType, seq, sampleRate, (byte) 1, (byte) 16);
        return buf;
    }

    public static boolean isValidHeader(byte[] data, int length) {
        if (data == null || length < HEADER_SIZE) {
            return false;
        }
        return data[0] == (byte) 'P'
                && data[1] == (byte) '2'
                && data[2] == (byte) 'W'
                && data[3] == (byte) 'M'
                && data[4] == VERSION;
    }

    public static void writeUInt32LE(byte[] buf, int offset, int value) {
        buf[offset] = (byte) (value & 0xFF);
        buf[offset + 1] = (byte) ((value >>> 8) & 0xFF);
        buf[offset + 2] = (byte) ((value >>> 16) & 0xFF);
        buf[offset + 3] = (byte) ((value >>> 24) & 0xFF);
    }

    public static int readUInt32LE(byte[] buf, int offset) {
        return (buf[offset] & 0xFF)
                | ((buf[offset + 1] & 0xFF) << 8)
                | ((buf[offset + 2] & 0xFF) << 16)
                | ((buf[offset + 3] & 0xFF) << 24);
    }
}
