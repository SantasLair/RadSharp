# Control reference

Generated from the component metadata by `tools/RadSharp.DocGen`; do not edit by hand.
Every control also has the [common properties and events](#common-to-every-control) listed at the end.
Defaults are the class defaults at 100 % scale; only values that differ are written to form files.

- [Button](#button)
- [CheckBox](#checkbox)
- [ColorBox](#colorbox)
- [ComboBox](#combobox)
- [Edit](#edit)
- [FolderDialog](#folderdialog) (non-visual)
- [Form](#form)
- [GroupBox](#groupbox)
- [Image](#image)
- [ImageList](#imagelist) (non-visual)
- [Label](#label)
- [ListBox](#listbox)
- [ListView](#listview)
- [MainMenu](#mainmenu)
- [Memo](#memo)
- [OpenDialog](#opendialog) (non-visual)
- [PageControl](#pagecontrol)
- [PaintBox](#paintbox)
- [Panel](#panel)
- [PopupMenu](#popupmenu) (non-visual)
- [ProgressBar](#progressbar)
- [PropertyGrid](#propertygrid)
- [RadioButton](#radiobutton)
- [SaveDialog](#savedialog) (non-visual)
- [ScrollBar](#scrollbar)
- [ScrollBox](#scrollbox)
- [SpinEdit](#spinedit)
- [Splitter](#splitter)
- [StatusBar](#statusbar)
- [TabSheet](#tabsheet)
- [Timer](#timer) (non-visual)
- [ToolBar](#toolbar)
- [ToolButton](#toolbutton)
- [ToolSeparator](#toolseparator)
- [TrackBar](#trackbar)
- [TreeView](#treeview)

## Button


| Property | Type | Default | Category |
|---|---|---|---|
| `cancel` | bool | `false` | Behavior |
| `caption` | string | "" | Appearance |
| `default` | bool | `false` | Behavior |
| `modalResult` | enum: none, ok, cancel, abort, retry, ignore, yes, no, close | `none` | Behavior |

## CheckBox

Inherits from [ToggleControl](#togglecontrol).

| Property | Type | Default | Category |
|---|---|---|---|
| `caption` | string | "" | Appearance |
| `checked` | bool | `false` | Behavior |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |

## ColorBox

Inherits from [ComboBox](#combobox).

| Property | Type | Default | Category |
|---|---|---|---|
| `dropDownCount` | int | `8` | Behavior |
| `itemIndex` | int | `0` | Behavior |
| `items` | strings | `Black\nMaroon\nGreen\nOlive\nNavy\nPurple\nTeal\nGray\nSilver\nRed\nLime\nYellow\nBlue\nFuchsia\nAqua\nWhite` | Behavior |
| `selected` | color | `#000000` | Behavior |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |

## ComboBox


| Property | Type | Default | Category |
|---|---|---|---|
| `dropDownCount` | int | `8` | Behavior |
| `itemIndex` | int | `-1` | Behavior |
| `items` | strings | "" | Behavior |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |

## Edit

Inherits from [EditBase](#editbase).

| Property | Type | Default | Category |
|---|---|---|---|
| `autoSelect` | bool | `true` | Behavior |
| `borderStyle` | enum: none, single | `single` | Appearance |
| `hideSelection` | bool | `true` | Behavior |
| `maxLength` | int | `0` | Behavior |
| `passwordChar` | string | "" | Behavior |
| `placeholder` | string | "" | Appearance |
| `readOnly` | bool | `false` | Behavior |
| `text` | string | "" | Behavior |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |

## FolderDialog

Inherits from [FileDialog](#filedialog).

| Property | Type | Default | Category |
|---|---|---|---|
| `fileName` | string | "" | Behavior |
| `filter` | string | "" | Behavior |
| `initialDir` | string | "" | Behavior |
| `title` | string | "" | Behavior |

## Form


| Property | Type | Default | Category |
|---|---|---|---|
| `caption` | string | "" | Appearance |
| `color` | color | "" | Appearance |

| Event | Handler parameters |
|---|---|
| `onClose` | `(Control sender)` |
| `onCloseQuery` | `(Control sender, CloseQueryEventArgs e)` |
| `onShow` | `(Control sender)` |

## GroupBox


| Property | Type | Default | Category |
|---|---|---|---|
| `caption` | string | "" | Appearance |

## Image


| Property | Type | Default | Category |
|---|---|---|---|
| `picture` | string | "" | Appearance |
| `scaling` | enum: none, stretch, fit, center | `fit` | Appearance |

## ImageList

Inherits from [Component](#component).

| Property | Type | Default | Category |
|---|---|---|---|
| `images` | strings | "" | Appearance |

## Label


| Property | Type | Default | Category |
|---|---|---|---|
| `alignment` | enum: left, center, right | `left` | Appearance |
| `caption` | string | "" | Appearance |
| `fontBold` | bool | `false` | Appearance |
| `fontColor` | color | "" | Appearance |
| `fontSize` | int | `0` | Appearance |
| `layout` | enum: top, center, bottom | `top` | Appearance |

## ListBox


| Property | Type | Default | Category |
|---|---|---|---|
| `borderStyle` | enum: none, single | `single` | Appearance |
| `itemHeight` | int | `24` | Appearance |
| `itemIndex` | int | `-1` | Behavior |
| `items` | strings | "" | Behavior |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |
| `onDblClick` | `(Control sender)` |
| `onDrawItem` | `(Control sender, Canvas canvas, int index, Rect rect, bool selected)` |

## ListView


| Property | Type | Default | Category |
|---|---|---|---|
| `columns` | strings | "" | Appearance |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |
| `onColumnClick` | `(Control sender, int column)` |
| `onDblClick` | `(Control sender)` |

## MainMenu


## Memo

Inherits from [EditBase](#editbase).

| Property | Type | Default | Category |
|---|---|---|---|
| `borderStyle` | enum: none, single | `single` | Appearance |
| `hideSelection` | bool | `true` | Behavior |
| `maxLength` | int | `0` | Behavior |
| `placeholder` | string | "" | Appearance |
| `readOnly` | bool | `false` | Behavior |
| `text` | string | "" | Behavior |
| `wantReturns` | bool | `true` | Behavior |
| `wantTabs` | bool | `false` | Behavior |
| `wordWrap` | bool | `true` | Behavior |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |

## OpenDialog

Inherits from [FileDialog](#filedialog).

| Property | Type | Default | Category |
|---|---|---|---|
| `fileName` | string | "" | Behavior |
| `filter` | string | "" | Behavior |
| `initialDir` | string | "" | Behavior |
| `multiSelect` | bool | `false` | Behavior |
| `title` | string | "" | Behavior |

## PageControl


| Property | Type | Default | Category |
|---|---|---|---|
| `activePageIndex` | int | `0` | Behavior |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |

## PaintBox


| Property | Type | Default | Category |
|---|---|---|---|
| `color` | color | "" | Appearance |

| Event | Handler parameters |
|---|---|
| `onPaint` | `(Control sender, Canvas canvas)` |

## Panel


| Property | Type | Default | Category |
|---|---|---|---|
| `borderStyle` | enum: none, single | `single` | Appearance |
| `caption` | string | "" | Appearance |
| `color` | color | "" | Appearance |

## PopupMenu

Inherits from [Component](#component).

| Event | Handler parameters |
|---|---|
| `onPopup` | `(Control sender)` |

## ProgressBar


| Property | Type | Default | Category |
|---|---|---|---|
| `max` | int | `100` | Behavior |
| `min` | int | `0` | Behavior |
| `position` | int | `0` | Behavior |

## PropertyGrid


| Property | Type | Default | Category |
|---|---|---|---|
| `mode` | enum: properties, events | `properties` | Behavior |
| `nameWidth` | int | `120` | Appearance |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |

## RadioButton

Inherits from [ToggleControl](#togglecontrol).

| Property | Type | Default | Category |
|---|---|---|---|
| `caption` | string | "" | Appearance |
| `checked` | bool | `false` | Behavior |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |

## SaveDialog

Inherits from [FileDialog](#filedialog).

| Property | Type | Default | Category |
|---|---|---|---|
| `fileName` | string | "" | Behavior |
| `filter` | string | "" | Behavior |
| `initialDir` | string | "" | Behavior |
| `title` | string | "" | Behavior |

## ScrollBar


| Property | Type | Default | Category |
|---|---|---|---|
| `kind` | enum: horizontal, vertical | `horizontal` | Appearance |
| `largeChange` | int | `10` | Behavior |
| `max` | int | `100` | Behavior |
| `min` | int | `0` | Behavior |
| `pageSize` | int | `10` | Behavior |
| `position` | int | `0` | Behavior |
| `smallChange` | int | `1` | Behavior |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |

## ScrollBox


| Property | Type | Default | Category |
|---|---|---|---|
| `borderStyle` | enum: none, single | `single` | Appearance |
| `color` | color | "" | Appearance |

## SpinEdit

Inherits from [Edit](#edit).

| Property | Type | Default | Category |
|---|---|---|---|
| `autoSelect` | bool | `true` | Behavior |
| `borderStyle` | enum: none, single | `single` | Appearance |
| `hideSelection` | bool | `true` | Behavior |
| `increment` | int | `1` | Behavior |
| `maxLength` | int | `0` | Behavior |
| `maxValue` | int | `0` | Behavior |
| `minValue` | int | `0` | Behavior |
| `passwordChar` | string | "" | Behavior |
| `placeholder` | string | "" | Appearance |
| `readOnly` | bool | `false` | Behavior |
| `text` | string | `0` | Behavior |
| `value` | int | `0` | Behavior |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |
| `onValueChange` | `(Control sender)` |

## Splitter


| Property | Type | Default | Category |
|---|---|---|---|
| `minSize` | int | `30` | Behavior |

## StatusBar


| Property | Type | Default | Category |
|---|---|---|---|
| `simpleText` | string | "" | Appearance |

## TabSheet


| Property | Type | Default | Category |
|---|---|---|---|
| `caption` | string | "" | Appearance |

## Timer

Inherits from [Component](#component).

| Property | Type | Default | Category |
|---|---|---|---|
| `enabled` | bool | `true` | Behavior |
| `interval` | int | `1000` | Behavior |

| Event | Handler parameters |
|---|---|
| `onTimer` | `(Control sender)` |

## ToolBar


| Property | Type | Default | Category |
|---|---|---|---|
| `images` | string | "" | Appearance |

## ToolButton

Inherits from [Button](#button).

| Property | Type | Default | Category |
|---|---|---|---|
| `allowToggle` | bool | `false` | Behavior |
| `cancel` | bool | `false` | Behavior |
| `caption` | string | "" | Appearance |
| `default` | bool | `false` | Behavior |
| `down` | bool | `false` | Behavior |
| `imageIndex` | int | `-1` | Appearance |
| `modalResult` | enum: none, ok, cancel, abort, retry, ignore, yes, no, close | `none` | Behavior |

## ToolSeparator


## TrackBar


| Property | Type | Default | Category |
|---|---|---|---|
| `frequency` | int | `1` | Appearance |
| `max` | int | `10` | Behavior |
| `min` | int | `0` | Behavior |
| `orientation` | enum: horizontal, vertical | `horizontal` | Appearance |
| `position` | int | `0` | Behavior |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |

## TreeView


| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |
| `onDblClick` | `(Control sender)` |

## Common to every control

| Property | Type | Default | Category |
|---|---|---|---|
| `align` | enum: none, top, bottom, left, right, client | `none` | Layout |
| `anchors` | anchors | `left, top` | Layout |
| `cursor` | enum: default, iBeam, hand, sizeWE, sizeNS, sizeNWSE, sizeNESW, sizeAll, wait, notAllowed, crosshair | `default` | Appearance |
| `enabled` | bool | `true` | Behavior |
| `font` | font | "" | Appearance |
| `height` | int | `30` | Layout |
| `hint` | string | "" | Behavior |
| `left` | int | `0` | Layout |
| `margin` | struct | `0, 0, 0, 0` | Layout |
| `maxHeight` | int | `0` | Layout |
| `maxWidth` | int | `0` | Layout |
| `minHeight` | int | `0` | Layout |
| `minWidth` | int | `0` | Layout |
| `padding` | struct | `0, 0, 0, 0` | Layout |
| `popupMenu` | string | "" | Behavior |
| `tabOrder` | int | `-1` | Behavior |
| `tabStop` | bool | `false` | Behavior |
| `top` | int | `0` | Layout |
| `visible` | bool | `true` | Behavior |
| `width` | int | `100` | Layout |

| Event | Handler parameters |
|---|---|
| `onBlur` | `(Control sender)` |
| `onClick` | `(Control sender)` |
| `onFocus` | `(Control sender)` |
| `onKeyDown` | `(Control sender, KeyEventArgs e)` |
| `onKeyUp` | `(Control sender, KeyEventArgs e)` |
| `onMouseDown` | `(Control sender, MouseEventArgs e)` |
| `onMouseEnter` | `(Control sender)` |
| `onMouseLeave` | `(Control sender)` |
| `onMouseMove` | `(Control sender, MouseEventArgs e)` |
| `onMouseUp` | `(Control sender, MouseEventArgs e)` |
| `onMouseWheel` | `(Control sender, MouseWheelEventArgs e)` |
| `onResize` | `(Control sender)` |
| `onTextInput` | `(Control sender, TextInputEventArgs e)` |

