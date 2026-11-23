package lan.linktv;

import java.io.ByteArrayOutputStream;
import java.util.Arrays;

final class AvcAnnexB {
    private static final byte[] START = {0, 0, 0, 1};

    static final class ParameterSets {
        byte[] sps;
        byte[] pps;

        boolean accept(byte[] data, int from) {
            boolean idr = false;
            int start = prefix(data, from);
            while (start >= 0) {
                int header = start + (data[start + 2] == 1 ? 3 : 4);
                if (header >= data.length) {
                    break;
                }
                int next = prefix(data, header + 1);
                int end = next < 0 ? data.length : next;
                int type = data[header] & 31;
                if (type == 7) {
                    sps = withPrefix(data, header, end);
                } else if (type == 8) {
                    pps = withPrefix(data, header, end);
                } else if (type == 5) {
                    idr = true;
                }
                start = next;
            }
            return idr;
        }

        boolean ready() {
            return sps != null && pps != null;
        }

        byte[] prependTo(byte[] frame) {
            if (!ready()) {
                throw new IllegalStateException("AVC parameter sets are missing");
            }
            byte[] combined = Arrays.copyOf(sps, sps.length + pps.length + frame.length);
            System.arraycopy(pps, 0, combined, sps.length, pps.length);
            System.arraycopy(frame, 0, combined, sps.length + pps.length, frame.length);
            return combined;
        }
    }

    private AvcAnnexB() {}

    static byte[] normalize(byte[] data) {
        if (data.length == 0) {
            return data;
        }
        if (prefix(data, 0) == 0) {
            return data;
        }
        byte[] lengths = fromLengths(data);
        if (lengths != null) {
            return lengths;
        }
        int type = data[0] & 31;
        if (type >= 1 && type <= 23 && (data[0] & 128) == 0) {
            return withPrefix(data, 0, data.length);
        }
        throw new IllegalArgumentException("Invalid AVC access unit");
    }

    private static byte[] fromLengths(byte[] data) {
        int cursor = 0;
        ByteArrayOutputStream result = new ByteArrayOutputStream(data.length);
        while (cursor + 4 < data.length) {
            long length = ((long) (data[cursor] & 255) << 24)
                    | ((long) (data[cursor + 1] & 255) << 16)
                    | ((long) (data[cursor + 2] & 255) << 8) | (data[cursor + 3] & 255);
            cursor += 4;
            if (length == 0 || length > data.length - cursor) {
                return null;
            }
            result.write(START, 0, START.length);
            result.write(data, cursor, (int) length);
            cursor += (int) length;
        }
        return cursor == data.length && result.size() > 0 ? result.toByteArray() : null;
    }

    private static byte[] withPrefix(byte[] data, int from, int end) {
        byte[] result = Arrays.copyOf(START, START.length + end - from);
        System.arraycopy(data, from, result, START.length, end - from);
        return result;
    }

    private static int prefix(byte[] data, int from) {
        for (int i = from; i + 2 < data.length; i++) {
            if (data[i] == 0 && data[i + 1] == 0 && (data[i + 2] == 1
                    || (i + 3 < data.length && data[i + 2] == 0 && data[i + 3] == 1))) {
                return i;
            }
        }
        return -1;
    }
}
