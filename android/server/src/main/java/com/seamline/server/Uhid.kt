package com.seamline.server

import android.system.Os
import android.system.OsConstants
import java.io.FileDescriptor
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Um mouse ou teclado "de mentira" criado pelo /dev/uhid do Linux. Para o Android ele é igual a um
 * aparelho USB de verdade: por isso aparece a setinha do mouse e o teclado físico funciona com o
 * layout escolhido nas configurações. É o mesmo truque do `--mouse=uhid` do scrcpy.
 * O usuário "shell" pode abrir o /dev/uhid porque faz parte do grupo "uhid".
 *
 * Cada aparelho usa um descritor de arquivo próprio; fechar o descritor remove o aparelho.
 */
class UhidDevice private constructor(private val fd: FileDescriptor) : AutoCloseable {
    private val inputBuffer = ByteBuffer.allocate(6 + MAX_REPORT_SIZE).order(ByteOrder.nativeOrder())
    private var closed = false

    /** Envia um "relatório" HID: o pacote que o mouse/teclado manda a cada mudança. */
    @Synchronized
    fun send(report: ByteArray) {
        if (closed) return
        inputBuffer.clear()
        inputBuffer.putInt(UHID_INPUT2)
        inputBuffer.putShort(report.size.toShort())
        inputBuffer.put(report)
        write(fd, inputBuffer.array(), inputBuffer.position())
    }

    @Synchronized
    override fun close() {
        if (closed) return
        closed = true
        Os.close(fd)
    }

    companion object {
        private const val UHID_CREATE2 = 11
        private const val UHID_INPUT2 = 12
        private const val BUS_VIRTUAL: Short = 0x06
        private const val MAX_REPORT_SIZE = 64

        /** [descriptor] diz ao sistema que tipo de aparelho é e o formato dos relatórios (veja Hid). */
        fun create(name: String, descriptor: ByteArray): UhidDevice {
            val fd = Os.open("/dev/uhid", OsConstants.O_RDWR, 0)
            try {
                write(fd, create2Request(name, descriptor))
            } catch (e: Exception) {
                Os.close(fd)
                throw e
            }
            return UhidDevice(fd)
        }

        /**
         * struct uhid_event { u32 type; struct uhid_create2_req { u8 name[128]; u8 phys[64]; u8 uniq[64];
         * u16 rd_size; u16 bus; u32 vendor; u32 product; u32 version; u32 country; u8 rd_data[]; } }
         */
        private fun create2Request(name: String, descriptor: ByteArray): ByteArray {
            val buffer = ByteBuffer.allocate(280 + descriptor.size).order(ByteOrder.nativeOrder())
            buffer.putInt(UHID_CREATE2)
            val nameBytes = name.toByteArray(Charsets.UTF_8).copyOf(127)
            buffer.put(nameBytes).put(0)
            buffer.position(buffer.position() + 64 + 64) // phys e uniq vazios
            buffer.putShort(descriptor.size.toShort())
            buffer.putShort(BUS_VIRTUAL)
            buffer.putInt(0) // vendor
            buffer.putInt(0) // product
            buffer.putInt(0) // version
            buffer.putInt(0) // country
            buffer.put(descriptor)
            return buffer.array()
        }

        private fun write(fd: FileDescriptor, data: ByteArray, length: Int = data.size) {
            var offset = 0
            while (offset < length) offset += Os.write(fd, data, offset, length - offset)
        }
    }
}

/** Descritores e relatórios HID do mouse e do teclado (formato da especificação USB HID). */
object Hid {
    /** Mouse com 5 botões, X/Y relativos de 16 bits, roda vertical e horizontal. */
    val MOUSE_DESCRIPTOR = bytes(
        0x05, 0x01, // Usage Page (Generic Desktop)
        0x09, 0x02, // Usage (Mouse)
        0xA1, 0x01, // Collection (Application)
        0x09, 0x01, //   Usage (Pointer)
        0xA1, 0x00, //   Collection (Physical)
        0x05, 0x09, //     Usage Page (Button)
        0x19, 0x01, //     Usage Minimum (1)
        0x29, 0x05, //     Usage Maximum (5)
        0x15, 0x00, //     Logical Minimum (0)
        0x25, 0x01, //     Logical Maximum (1)
        0x95, 0x05, //     Report Count (5)
        0x75, 0x01, //     Report Size (1)
        0x81, 0x02, //     Input (Data, Variable, Absolute): os 5 botões
        0x95, 0x01, //     Report Count (1)
        0x75, 0x03, //     Report Size (3)
        0x81, 0x01, //     Input (Constant): completa o byte
        0x05, 0x01, //     Usage Page (Generic Desktop)
        0x09, 0x30, //     Usage (X)
        0x09, 0x31, //     Usage (Y)
        0x16, 0x01, 0x80, // Logical Minimum (-32767)
        0x26, 0xFF, 0x7F, // Logical Maximum (32767)
        0x75, 0x10, //     Report Size (16)
        0x95, 0x02, //     Report Count (2)
        0x81, 0x06, //     Input (Data, Variable, Relative): X e Y
        0x09, 0x38, //     Usage (Wheel)
        0x15, 0x81, //     Logical Minimum (-127)
        0x25, 0x7F, //     Logical Maximum (127)
        0x75, 0x08, //     Report Size (8)
        0x95, 0x01, //     Report Count (1)
        0x81, 0x06, //     Input (Data, Variable, Relative): roda
        0x05, 0x0C, //     Usage Page (Consumer)
        0x0A, 0x38, 0x02, // Usage (AC Pan)
        0x95, 0x01, //     Report Count (1)
        0x81, 0x06, //     Input (Data, Variable, Relative): roda horizontal
        0xC0,       //   End Collection
        0xC0,       // End Collection
    )

    /** Teclado padrão (igual ao modo "boot" da BIOS): modificadores + até 6 teclas ao mesmo tempo. */
    val KEYBOARD_DESCRIPTOR = bytes(
        0x05, 0x01, // Usage Page (Generic Desktop)
        0x09, 0x06, // Usage (Keyboard)
        0xA1, 0x01, // Collection (Application)
        0x05, 0x07, //   Usage Page (Keyboard)
        0x19, 0xE0, //   Usage Minimum (Left Control)
        0x29, 0xE7, //   Usage Maximum (Right GUI)
        0x15, 0x00, //   Logical Minimum (0)
        0x25, 0x01, //   Logical Maximum (1)
        0x75, 0x01, //   Report Size (1)
        0x95, 0x08, //   Report Count (8)
        0x81, 0x02, //   Input (Data, Variable, Absolute): Ctrl, Shift, Alt, Win
        0x95, 0x01, //   Report Count (1)
        0x75, 0x08, //   Report Size (8)
        0x81, 0x01, //   Input (Constant): byte reservado
        0x05, 0x08, //   Usage Page (LEDs)
        0x19, 0x01, //   Usage Minimum (Num Lock)
        0x29, 0x05, //   Usage Maximum (Kana)
        0x95, 0x05, //   Report Count (5)
        0x75, 0x01, //   Report Size (1)
        0x91, 0x02, //   Output (Data, Variable, Absolute): luzes do teclado
        0x95, 0x01, //   Report Count (1)
        0x75, 0x03, //   Report Size (3)
        0x91, 0x01, //   Output (Constant)
        0x05, 0x07, //   Usage Page (Keyboard)
        0x19, 0x00, //   Usage Minimum (0)
        0x29, 0xE7, //   Usage Maximum (0xE7)
        0x15, 0x00, //   Logical Minimum (0)
        0x26, 0xE7, 0x00, // Logical Maximum (0xE7): inclui as teclas extras do ABNT2
        0x75, 0x08, //   Report Size (8)
        0x95, 0x06, //   Report Count (6)
        0x81, 0x00, //   Input (Data, Array): as teclas apertadas
        0xC0,       // End Collection
    )

    /** dx/dy de -32767 a 32767; roda de -127 a 127 (positivo = para cima / para a direita). */
    fun mouseReport(buttons: Int, dx: Int, dy: Int, wheel: Int, pan: Int) = byteArrayOf(
        buttons.toByte(),
        dx.toByte(), (dx shr 8).toByte(),
        dy.toByte(), (dy shr 8).toByte(),
        wheel.toByte(),
        pan.toByte(),
    )

    /** [keys]: códigos de uso HID (página 0x07) das teclas apertadas, no máximo 6. */
    fun keyboardReport(modifiers: Int, keys: IntArray): ByteArray {
        val report = ByteArray(8)
        report[0] = modifiers.toByte()
        keys.take(6).forEachIndexed { i, key -> report[2 + i] = key.toByte() }
        return report
    }

    private fun bytes(vararg values: Int) = ByteArray(values.size) { values[it].toByte() }
}
