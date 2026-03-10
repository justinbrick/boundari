extends Control

@onready var slider = $VBox_Slider/HSlider
@onready var label = $"../HBoxContainer/Ratio"

func _ready():
	slider.value_changed.connect(_on_h_slider_value_changed)

func _on_h_slider_value_changed(value: float) -> void:
	label.text = str(int(value))
