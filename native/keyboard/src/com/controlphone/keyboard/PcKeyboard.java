package com.controlphone.keyboard;

import android.inputmethodservice.InputMethodService;
import android.view.View;

/**
 * Bàn phím "chỉ nhập từ PC" của ControlPhone: không bao giờ hiện bàn phím ảo trên màn hình điện thoại.
 * Phím, văn bản và lệnh dán (Ctrl+V) do ControlPhone gửi qua scrcpy vẫn đi thẳng vào ô nhập đang chọn.
 */
public class PcKeyboard extends InputMethodService {

    /** Không hiện bàn phím (kể cả khi chạm vào ô nhập). */
    @Override
    public boolean onEvaluateInputViewShown() {
        super.onEvaluateInputViewShown();
        return false;
    }

    /** Không chiếm toàn màn hình khi xoay ngang. */
    @Override
    public boolean onEvaluateFullscreenMode() {
        return false;
    }

    /** Khung bàn phím rỗng, cao 0 (phòng khi hệ thống vẫn yêu cầu hiển thị). */
    @Override
    public View onCreateInputView() {
        View v = new View(this);
        v.setMinimumHeight(0);
        return v;
    }
}
