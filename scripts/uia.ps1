# Minimal UI Automation driver for manual UI verification of CCNest (development aid).
param([string]$Action, [string]$Name)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "CCNest")
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { Write-Error "window not found"; exit 1 }
$byName = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
$el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $byName)
if (-not $el) {
  # list items whose content is a text block: match by descendant text
  $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  foreach ($e in $all) { if ($e.Current.Name -eq $Name) { $el = $e; break } }
}
if (-not $el) { Write-Error "element '$Name' not found"; exit 2 }
switch ($Action) {
  "click" {
    $p = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); "invoked $Name"; break }
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $parent = $walker.GetParent($el)
    while ($parent) {
      if ($parent.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { $p.Select(); "selected parent of $Name"; break }
      if ($parent.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); "invoked parent of $Name"; break }
      $parent = $walker.GetParent($parent)
    }
  }
}
