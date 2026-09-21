using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;

namespace SpeakCity;

/// <summary>
/// The window's visual language: the same palette and shapes the HTML interface
/// uses (teal #196D69, mint #E8F4EF, ink #162F3C, 10-15 px radii), expressed as
/// WPF styles so the native window looks like part of the same product instead of
/// a row of stock grey controls.
///
/// Everything in the XAML below is stock WPF markup with StaticResource lookups
/// that all resolve top-down, so the dictionary cannot fail to load. Anything
/// that needs a per-item decision (chat bubbles, scenario cards) is built in C#
/// and only borrows the brushes and styles from here.
/// </summary>
public static class NativeTheme
{
    public static ResourceDictionary Dictionary { get; } = (ResourceDictionary)XamlReader.Parse(Xaml);

    public static Style Style(string key) => (Style)Dictionary[key];

    public static Brush Brush(string key) => (Brush)Dictionary[key];

    private const string Xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

  <SolidColorBrush x:Key="Ink" Color="#FF162F3C"/>
  <SolidColorBrush x:Key="Muted" Color="#FF5A727A"/>
  <SolidColorBrush x:Key="Teal" Color="#FF196D69"/>
  <SolidColorBrush x:Key="TealDark" Color="#FF125653"/>
  <SolidColorBrush x:Key="Mint" Color="#FFE8F4EF"/>
  <SolidColorBrush x:Key="Line" Color="#FFDCE6E6"/>
  <SolidColorBrush x:Key="Bg" Color="#FFF7F8F3"/>
  <SolidColorBrush x:Key="Coral" Color="#FFE98C72"/>
  <SolidColorBrush x:Key="White" Color="#FFFFFFFF"/>
  <SolidColorBrush x:Key="Hover" Color="#FFF2F7F6"/>
  <SolidColorBrush x:Key="OnTeal" Color="#FFCBEAE4"/>

  <Style x:Key="Heading" TargetType="TextBlock">
    <Setter Property="FontSize" Value="21"/>
    <Setter Property="FontWeight" Value="SemiBold"/>
    <Setter Property="Foreground" Value="{StaticResource Ink}"/>
  </Style>

  <Style x:Key="Label" TargetType="TextBlock">
    <Setter Property="FontSize" Value="12"/>
    <Setter Property="FontWeight" Value="SemiBold"/>
    <Setter Property="Foreground" Value="{StaticResource Muted}"/>
  </Style>

  <Style x:Key="Body" TargetType="TextBlock">
    <Setter Property="FontSize" Value="13"/>
    <Setter Property="Foreground" Value="{StaticResource Ink}"/>
    <Setter Property="TextWrapping" Value="Wrap"/>
  </Style>

  <Style x:Key="Card" TargetType="Border">
    <Setter Property="Background" Value="{StaticResource White}"/>
    <Setter Property="BorderBrush" Value="{StaticResource Line}"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="CornerRadius" Value="15"/>
    <Setter Property="Padding" Value="16"/>
  </Style>

  <Style x:Key="Pill" TargetType="Border">
    <Setter Property="Background" Value="{StaticResource Mint}"/>
    <Setter Property="CornerRadius" Value="20"/>
    <Setter Property="Padding" Value="11,5"/>
    <Setter Property="Margin" Value="0,0,7,7"/>
  </Style>

  <Style x:Key="PrimaryButton" TargetType="Button">
    <Setter Property="Background" Value="{StaticResource Teal}"/>
    <Setter Property="Foreground" Value="{StaticResource White}"/>
    <Setter Property="FontWeight" Value="SemiBold"/>
    <Setter Property="Padding" Value="18,10"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border x:Name="Bd" Background="{TemplateBinding Background}" CornerRadius="10"
                  Padding="{TemplateBinding Padding}">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="Bd" Property="Background" Value="{StaticResource TealDark}"/>
            </Trigger>
            <Trigger Property="IsPressed" Value="True">
              <Setter TargetName="Bd" Property="Opacity" Value="0.86"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter TargetName="Bd" Property="Opacity" Value="0.4"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key="OutlineButton" TargetType="Button">
    <Setter Property="Background" Value="{StaticResource White}"/>
    <Setter Property="Foreground" Value="{StaticResource Ink}"/>
    <Setter Property="FontWeight" Value="SemiBold"/>
    <Setter Property="Padding" Value="16,10"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border x:Name="Bd" Background="{TemplateBinding Background}"
                  BorderBrush="{StaticResource Line}" BorderThickness="1"
                  CornerRadius="10" Padding="{TemplateBinding Padding}">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="Bd" Property="Background" Value="{StaticResource Mint}"/>
              <Setter TargetName="Bd" Property="BorderBrush" Value="{StaticResource Teal}"/>
            </Trigger>
            <Trigger Property="IsPressed" Value="True">
              <Setter TargetName="Bd" Property="Opacity" Value="0.86"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter TargetName="Bd" Property="Opacity" Value="0.4"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key="MicButton" TargetType="ToggleButton">
    <Setter Property="Foreground" Value="{StaticResource Teal}"/>
    <Setter Property="FontWeight" Value="SemiBold"/>
    <Setter Property="Padding" Value="20,11"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ToggleButton">
          <Border x:Name="Bd" Background="{StaticResource Mint}"
                  BorderBrush="{StaticResource Teal}" BorderThickness="1.5"
                  CornerRadius="22" Padding="{TemplateBinding Padding}">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsChecked" Value="True">
              <Setter TargetName="Bd" Property="Background" Value="{StaticResource Coral}"/>
              <Setter TargetName="Bd" Property="BorderBrush" Value="{StaticResource Coral}"/>
              <Setter Property="Foreground" Value="{StaticResource White}"/>
            </Trigger>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="Bd" Property="Opacity" Value="0.9"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter TargetName="Bd" Property="Opacity" Value="0.4"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key="PlainList" TargetType="ListBox">
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="BorderThickness" Value="0"/>
    <Setter Property="Padding" Value="4"/>
    <Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Disabled"/>
  </Style>

  <Style x:Key="ScenarioItem" TargetType="ListBoxItem">
    <Setter Property="Margin" Value="0,0,0,8"/>
    <Setter Property="Padding" Value="0"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ListBoxItem">
          <Border x:Name="Card" Background="{StaticResource White}"
                  BorderBrush="{StaticResource Line}" BorderThickness="1"
                  CornerRadius="12" Padding="13,11">
            <ContentPresenter/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="Card" Property="Background" Value="{StaticResource Hover}"/>
            </Trigger>
            <Trigger Property="IsSelected" Value="True">
              <Setter TargetName="Card" Property="Background" Value="{StaticResource Mint}"/>
              <Setter TargetName="Card" Property="BorderBrush" Value="{StaticResource Teal}"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key="ScenarioList" TargetType="ListBox" BasedOn="{StaticResource PlainList}">
    <Setter Property="ItemContainerStyle" Value="{StaticResource ScenarioItem}"/>
  </Style>

  <Style x:Key="InputBox" TargetType="TextBox">
    <Setter Property="FontSize" Value="14"/>
    <Setter Property="Foreground" Value="{StaticResource Ink}"/>
    <Setter Property="CaretBrush" Value="{StaticResource Teal}"/>
    <Setter Property="Padding" Value="13,10"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="TextBox">
          <Border x:Name="Bd" Background="{StaticResource White}"
                  BorderBrush="{StaticResource Line}" BorderThickness="1"
                  CornerRadius="10" Padding="{TemplateBinding Padding}">
            <ScrollViewer x:Name="PART_ContentHost" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsKeyboardFocused" Value="True">
              <Setter TargetName="Bd" Property="BorderBrush" Value="{StaticResource Teal}"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter TargetName="Bd" Property="Background" Value="{StaticResource Bg}"/>
              <Setter TargetName="Bd" Property="Opacity" Value="0.7"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key="ScrollHost" TargetType="ScrollViewer">
    <Setter Property="VerticalScrollBarVisibility" Value="Auto"/>
    <Setter Property="HorizontalScrollBarVisibility" Value="Disabled"/>
    <Setter Property="Focusable" Value="False"/>
  </Style>

</ResourceDictionary>
""";
}