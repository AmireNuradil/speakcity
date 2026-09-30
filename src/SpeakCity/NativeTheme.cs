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
/// that needs a per-item decision (chat bubbles, map pins) is built in C#
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
    <Style.Triggers>
      <Trigger Property="IsMouseOver" Value="True">
        <Setter Property="Background" Value="#FFD3EBE2"/>
      </Trigger>
    </Style.Triggers>
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

  <Style x:Key="RoundMic" TargetType="ToggleButton">
    <Setter Property="Foreground" Value="{StaticResource White}"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="Width" Value="58"/>
    <Setter Property="Height" Value="58"/>
    <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ToggleButton">
          <Grid>
            <Ellipse x:Name="Ring" Fill="#FFEDF5F2"/>
            <Ellipse x:Name="Dot" Fill="{StaticResource Teal}" Margin="5"/>
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="Dot" Property="Fill" Value="{StaticResource TealDark}"/>
            </Trigger>
            <Trigger Property="IsChecked" Value="True">
              <Setter TargetName="Ring" Property="Fill" Value="#FFFAE8E1"/>
              <Setter TargetName="Dot" Property="Fill" Value="#FFB04B3C"/>
            </Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True">
              <Setter TargetName="Ring" Property="Stroke" Value="{StaticResource Ink}"/>
              <Setter TargetName="Ring" Property="StrokeThickness" Value="2"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Opacity" Value="0.4"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key="LinkButton" TargetType="Button">
    <Setter Property="Foreground" Value="{StaticResource Teal}"/>
    <Setter Property="FontSize" Value="11"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border x:Name="Bd" Background="Transparent" BorderBrush="Transparent" BorderThickness="1"
                  CornerRadius="6" Padding="5,2">
            <ContentPresenter VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="Bd" Property="Background" Value="{StaticResource Mint}"/>
            </Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True">
              <Setter TargetName="Bd" Property="BorderBrush" Value="{StaticResource Teal}"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter TargetName="Bd" Property="Opacity" Value="0.4"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- One place on the city map. Colours and size are set per pin from C#
       (selected vs not); hover grows it like the web's scale(1.04). -->
  <Style x:Key="Pin" TargetType="Button">
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="RenderTransformOrigin" Value="0.5,0.5"/>
    <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border x:Name="Focus" BorderBrush="Transparent" BorderThickness="2" CornerRadius="13" Padding="1">
            <Grid>
              <Border x:Name="Shade" Background="{TemplateBinding Background}" CornerRadius="10">
                <Border.Effect>
                  <DropShadowEffect Color="#FF122D32" BlurRadius="9" ShadowDepth="3" Direction="270" Opacity="0.18"/>
                </Border.Effect>
              </Border>
              <Border x:Name="Bd" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                      BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="10"
                      Padding="{TemplateBinding Padding}">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Border>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsPressed" Value="True">
              <Setter TargetName="Bd" Property="Opacity" Value="0.88"/>
            </Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True">
              <Setter TargetName="Focus" Property="BorderBrush" Value="{StaticResource Ink}"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
    <Style.Triggers>
      <Trigger Property="IsMouseOver" Value="True">
        <Setter Property="RenderTransform">
          <Setter.Value>
            <ScaleTransform ScaleX="1.05" ScaleY="1.05"/>
          </Setter.Value>
        </Setter>
      </Trigger>
    </Style.Triggers>
  </Style>

  <!-- A whole card that is one button: Settings sections and the choices on their pages.
       A selected choice gets a mint background and a teal border from C#. -->
  <Style x:Key="CardButton" TargetType="Button">
    <Setter Property="Background" Value="{StaticResource White}"/>
    <Setter Property="Foreground" Value="{StaticResource Ink}"/>
    <Setter Property="BorderBrush" Value="{StaticResource Line}"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="Padding" Value="18,14"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
    <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border x:Name="Bd" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                  BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="14" Padding="{TemplateBinding Padding}">
            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="Bd" Property="BorderBrush" Value="{StaticResource Teal}"/>
            </Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True">
              <Setter TargetName="Bd" Property="BorderBrush" Value="{StaticResource Ink}"/>
            </Trigger>
            <Trigger Property="IsPressed" Value="True">
              <Setter TargetName="Bd" Property="Opacity" Value="0.9"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
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