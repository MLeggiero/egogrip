# Expose a single USB-CDC "data" serial (no REPL console) so the Android host opens exactly
# one port and there's no ambiguity for usb-serial-for-android.
#
# To DEBUG on your PC later (get the REPL back), set console=True temporarily and re-copy.
import usb_cdc

usb_cdc.enable(console=False, data=True)
