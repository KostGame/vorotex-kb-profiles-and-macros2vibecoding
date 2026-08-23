#Requires AutoHotkey v2.0
#SingleInstance Force

; VOROTEX K15 Pro VIBECODING v1 alpha dispatcher.
; Hardware macros type layout-independent numeric sentinels.
; AutoHotkey removes the sentinel and emits Unicode text or an action.
;
; Consequential commands intentionally insert text only. They do not submit.

:*:77133701::Проверь
:*:77133702::Следующий шаг
:*:77133703::Пиши следующий промпт для агента
:*:77133704::Исправляй
:*:77133705::Публикуй
:*:77133706::Мержи
:*:77133707::Создавай
:*:77133708::Продолжай
:*:77133709::Проведи review
:*:77133710::Запусти тесты
:*:77133711::Дай статус

; NEW_LINE alpha behavior.
; Shift+Enter is verified as the intended ChatGPT-style multiline gesture.
; App-specific Codex/IDE rules will be added after calibration.
:*:77133712::
{
    Send("+{Enter}")
}

:*:77133713::Стоп

; Temporary SUBMIT key. Planned destination is joystick click after its
; programmable storage path is confirmed.
:*:77133714::
{
    Send("{Enter}")
}

:*:77133715::Принимается
