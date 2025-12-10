extends Button

@export var target_path: NodePath
var expanded := false

func _ready() -> void:
    pressed.connect(_toggle)

    if target_path != NodePath():
        var tgt = get_node_or_null(target_path)
        if tgt:
            tgt.visible = false

func _toggle() -> void:
    var tgt = get_node_or_null(target_path)
    if tgt == null:
        return

    expanded = !expanded
    tgt.visible = expanded