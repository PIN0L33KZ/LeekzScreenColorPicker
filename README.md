# Leekz.ScreenColorPicker

Category: Library, Development, UI-Tools  
Status: Production  
Last Maintenance: 2026-03-09  
Version: 1.0.0.0

---

## Short Description

### What is it?

A WinForms user control that provides a screen colour picker and extensive configuration and customisation options.

### What is it used for?

It can be used in any scenario where precise colour selection from the screen is required, such as design tools, UI development utilities, or graphics-related applications. Thanks to its flexible behaviour and visual customisation options, the control can be integrated easily into a wide range of Windows Forms projects that need a reliable and efficient colour picking component.

---

## Requirements & Dependencies

### Operating Systems

Windows 10 (and above)

### Services

Visual Studio, Winforms, .NET 10.0 (and above)

---

## Installation & Setup

### Installation Method

==Package==

via NuGet-CLI:

```markdown
dotnet add package Leekz.ScreenColorPicker
```

or via the Graphical Installer built into Visual Studio:

```markdown
Project -> Manage NuGet Packages -> Search for Leekz.ScreenColorPicker
```

### Basic Configuration

Drag and Drop the Control from the Toolbox to your Designer Window, or use the Code below providing a default setup:

```csharp
var colorPicker = new ScreenColorPickerControl();
colorPicker.Size = new Size(200, 200);
colorPicker.Margin = new Padding(10);
colorPicker.Location = new Point(10, 10);

// Optional: handle the colour picked event
colorPicker.ColorPicked += (s, e) =>
{
  Console.WriteLine($"Picked colour: {e.HexColor}");
};

this.Controls.Add(colorPicker);
```

You can subscribe to Events like `ColorPicked` or `CurrentColorChanged` either via Delegates in your Code, or via the Graphical Panel in Visual Studio:

```csharp
private void colorPicker_ColorPicked(object? sender, ScreenColorPickedEventArgs e)
{
  // Handle the colour picked event
  Console.WriteLine($"Picked colour: {e.HexColor}");
}

// Add the following line to your Form's constructor:
colorPicker.ColorPicked += colorPicker_ColorPicked;
```

```csharp
private void colorPicker_CurrentColorChanged(object? sender, EventArgs e)
{
  if (sender is ScreenColorPickerControl picker)
  {
    // Access the live colour currently under the cursor
    Color currentColor = picker.CurrentColor;
    Console.WriteLine($"Current colour: #{currentColor.R:X2}{currentColor.G:X2     {currentColor.B:X2}");
  }
}

// Add the following line to your Form's constructor:
colorPicker.CurrentColorChanged += colorPicker_CurrentColorChanged;
```

---

## Usage

### How is it used?

After installing the package and adding the control to a form, either through the Visual Studio designer or by creating it in code, its behaviour and appearance can be configured using the available properties. These settings can be adjusted directly in the Visual Studio Properties window or programmatically within your application. Below are some of the options supported by the control:

```csharp
// Preview behaviour
colorPicker.Zoom = 12;
colorPicker.ShowGrid = true;
colorPicker.UseAdaptiveGridColor = true;

// Grid styling
colorPicker.DarkGridColor = Color.White;
colorPicker.LightGridColor = Color.Black;

// Control appearance
colorPicker.BackColor = Color.Transparent;
colorPicker.ForeColor = Color.White;

// Optional help text displayed when idle
colorPicker.Text = "Click and hold to pick a colour";
```

---

## Additional Links

### Website Links

[Project Page](https://pinoleekz.de/leekzscreencolorpicker) | [GitHub Page](https://github.com/PIN0L33KZ/LeekzScreenColorPicker) | [NuGet Page](https://www.nuget.org/packages/Leekz.ScreenColorPicker/#readme-body-tab)

---

## Last Update to Documentation

**Date:** 2026-03-09
