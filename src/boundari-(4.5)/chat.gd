extends ColorRect

@onready var chat_log: RichTextLabel = $RichTextLabel
@onready var chat_input: LineEdit = $LineEdit

func _ready() -> void:
	# Connect the signal in code (no need to use the Node tab)
	chat_input.text_submitted.connect(_on_chat_input_text_submitted)


func _on_chat_input_text_submitted(new_text: String) -> void:
	var msg := new_text.strip_edges()
	if msg == "":
		return

	# Add text to the chat box above
	chat_log.append_text(msg + "\n")

	# Clear input and keep focus so player can keep typing
	chat_input.text = ""
	chat_input.grab_focus()
