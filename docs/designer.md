# Installer Designer

Open a project and choose **Designer**. The workspace has the page list and toolbox on the left, the canvas in the middle, and the inspector and layers on the right.

## Pages
- Built-in page types (Welcome, License Agreement, Installation Directory, Component Selection, Prerequisites, User Configuration, Installation Progress,
  Installation Summary, Completion, Error, Uninstallation) are created with a default layout. **Custom** pages start with a title and navigation buttons.
- Add, duplicate, delete, reorder, and reset a page to its default layout. Deleting a page that other pages or buttons navigate to resets those references after confirmation.
- Page settings (select nothing on the canvas): title, background (theme token, colour or image asset), Allow Back / Allow Cancel, next/back page overrides,
  a "show page only when" condition, and the installer window size (shared by all pages). Page settings are saved immediately and are **not** in the undo history.

## Controls
Text, Rich Text (`**bold**`, `*italic*`, optional text asset), Image, Button, Text Input, Password Input, Checkbox, Radio Buttons, Dropdown, List
(items, project components or prerequisites), Progress Bar, Card, Stepper, Accordion, Separator, Icon, Container (absolute, vertical or horizontal layout), Group.
Every property is declared in the control's contract (`ControlRegistry`) and validated. Colours always come from theme tokens.

## Editing
- Drag controls from the toolbox (or double-click). Dropping on a card, accordion or container adds the element to it.
- Click to select (Shift/Ctrl to add, Alt to select inside a group), drag to move, use the handles to resize (Shift keeps aspect on corners), drag on empty canvas to marquee-select.
- Snapping aligns to other elements, the page edges/centre and an 8 px grid (toggle **Snap**). Arrow keys nudge by 1 px (Shift: 10 px).
- Ctrl+Z / Ctrl+Y undo and redo; Ctrl+C / X / V / D copy, cut, paste, duplicate; Ctrl+G / Ctrl+Shift+G group and ungroup; Delete removes. Undo history is kept per page while the project is open.
- Align (left/centre/right/top/middle/bottom), distribute, exact spacing, match width/height, and z-order (front/back/forward/backward) are in the toolbar. Layers lists the tree with hide and lock.
- Children of vertical/horizontal containers are positioned by the container, so only their size can be edited; reorder them with the layer buttons.

## Variables, conditions and actions
Inputs write to the variable named in their *Variable* property. Text may contain `{Name}` tokens (built-ins: ProductName, Version, Publisher, InstallDir, ErrorMessage, Status).
Elements and pages can have a visibility condition and, for inputs and buttons, an enabled condition (is true/false, equals, not equals, contains, is empty/not empty).
Button actions: Next, Back, Cancel, Finish, Go to page, Browse for folder, Set variable.

## Custom controls
Select elements and choose **Create from selection**. Variable names in the template become parameters automatically, so each instance can bind its own variable.
**Edit selected control** opens the template on the canvas; type `{param:Name}` in property values to bind them to parameters, and manage parameters (text, bool, number, choice) in the inspector.
Controls are stored in the project; **Save to library** / **Add from library** / import / export (`.paiccontrol`) reuse them across projects. Custom controls cannot contain other custom controls.
Instances are rendered by expanding the template, so they behave as one atomic element in the designer.

## Preview
The **Preview** toggle walks through the installer pages with the chosen theme, window size and display scale (100-200 %). Buttons, conditions, navigation and inputs work,
and you can edit variables to exercise conditions. **This is a visual preview only: nothing is installed and no files are changed.** The progress bar uses a simulated value.

## Limitations
- Layout is absolute (or stack-based inside containers); pages do not reflow when the window size changes, so the preview shows how fixed positions look in other sizes.
- Accordion headers do not toggle in the preview; expanded state is a property.
- Installer pages are rendered with WinUI controls inside the designer. The generated installer runtime (a later phase) will interpret the same model.
- Page-level settings, window size and custom control definitions are not part of the canvas undo history.
