using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Interactivity;
using Xiaomuocr.Core.Models;
using Xiaomuocr.Core.ViewModels;

namespace Xiaomuocr.Views.Views;

public partial class InvoiceView : UserControl
{
    public InvoiceView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireVm();
        Loaded += (_, _) => WireVm();
    }

    private void WireVm()
    {
        if (DataContext is not InvoiceViewModel vm) return;
        vm.OpenCreateDialogAsync = () => OpenEditorAsync(vm, null);
        vm.OpenDetailDialogAsync = item => OpenEditorAsync(vm, item);
    }

    private async void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border b || b.Tag is not InvoiceListItem item) return;
        if (DataContext is not InvoiceViewModel vm) return;
        await vm.OpenItemAsync(item);
    }

    private async Task OpenEditorAsync(InvoiceViewModel vm, InvoiceListItem? listItem)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return;

        InvoiceDetail? detail = null;
        List<InvoiceOrderBrief> eligible;
        try
        {
            eligible = await vm.LoadEligibleOrdersAsync();
            if (listItem != null)
                detail = await vm.GetDetailAsync(listItem.Id);
        }
        catch (Exception ex)
        {
            vm.ErrorMessage = ex.Message;
            return;
        }

        var editable = detail?.Editable ?? true;
        var isNew = detail == null;

        // 合并：详情中的订单 + 可勾选新订单
        var orderPanel = new StackPanel { Spacing = 4 };
        var orderChecks = new List<(CheckBox Cb, int OrderId)>();
        var selectedIds = new HashSet<int>(detail?.Orders.Select(o => o.Id) ?? Enumerable.Empty<int>());

        void AddOrderRow(InvoiceOrderBrief o, bool check, bool enabled)
        {
            var cb = new CheckBox
            {
                IsChecked = check,
                IsEnabled = enabled && editable,
                Content = $"¥{o.Amount:F2}  {o.ProductName}  ({o.OutTradeNo})",
                FontSize = 12,
            };
            orderChecks.Add((cb, o.Id));
            orderPanel.Children.Add(cb);
        }

        if (detail != null)
        {
            foreach (var o in detail.Orders)
                AddOrderRow(o, true, true);
        }
        foreach (var o in eligible)
        {
            if (selectedIds.Contains(o.Id)) continue;
            AddOrderRow(o, false, true);
        }

        if (orderPanel.Children.Count == 0)
            orderPanel.Children.Add(new TextBlock
            {
                Text = isNew ? "暂无可开票的已支付订单" : "无关联订单",
                FontSize = 12,
                Foreground = Brush.Parse("#8B8370"),
            });

        var titleTypeCombo = new ComboBox
        {
            Width = 120,
            IsEnabled = editable,
            SelectedIndex = (detail?.TitleType == "company") ? 1 : 0,
        };
        titleTypeCombo.Items.Add("个人");
        titleTypeCombo.Items.Add("企业");
        titleTypeCombo.SelectedIndex = (detail?.TitleType == "company") ? 1 : 0;
        var titleNameBox = NewBox(detail?.TitleName ?? "", editable);
        var taxBox = NewBox(detail?.TaxNo ?? "", editable, "企业税号");
        var addrBox = NewBox(detail?.AddressPhone ?? "", editable, "地址电话（选填）");
        var bankBox = NewBox(detail?.BankAccount ?? "", editable, "开户行及账号（选填）");
        var remarkBox = NewBox(detail?.Remark ?? "", editable, "发票备注（选填）");
        remarkBox.AcceptsReturn = true;
        remarkBox.Height = 56;
        remarkBox.TextWrapping = TextWrapping.Wrap;
        var emailBox = NewBox(detail?.Email ?? "", editable, "接收邮箱");
        var phoneBox = NewBox(detail?.Phone ?? "", editable, "接收手机");

        var msgPanel = new StackPanel { Spacing = 6 };
        var messages = (detail?.Messages ?? new List<InvoiceMessageItem>())
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .ToList();
        // 仅有 admin_note 时补一条
        if (!string.IsNullOrWhiteSpace(detail?.AdminNote)
            && !messages.Any(m =>
                string.Equals(m.SenderRole, "admin", StringComparison.OrdinalIgnoreCase)
                && (m.Content?.Contains(detail!.AdminNote!, StringComparison.Ordinal) ?? false)))
        {
            messages.Add(new InvoiceMessageItem
            {
                Id = 0,
                SenderRole = "admin",
                Content = $"[处理备注] {detail!.AdminNote}",
                CreatedAt = detail.UpdatedAt ?? detail.CompletedAt,
            });
        }
        // 已处理但无管理员消息时，按状态合成，避免详情空白
        if (detail != null
            && !messages.Any(m => string.Equals(m.SenderRole, "admin", StringComparison.OrdinalIgnoreCase)))
        {
            var synth = detail.Status switch
            {
                "processing" => "管理员正在处理您的开票申请",
                "completed" => "管理员已完成开票处理",
                "rejected" => "管理员已拒绝此开票申请",
                _ => null,
            };
            if (synth != null)
            {
                messages.Add(new InvoiceMessageItem
                {
                    Id = 0,
                    SenderRole = "admin",
                    Content = synth,
                    CreatedAt = detail.CompletedAt ?? detail.UpdatedAt,
                });
            }
        }

        // 管理员回执置顶显示
        foreach (var m in messages
                     .OrderByDescending(m => string.Equals(m.SenderRole, "admin", StringComparison.OrdinalIgnoreCase))
                     .ThenBy(m => m.CreatedAt))
        {
            var isAdmin = string.Equals(m.SenderRole, "admin", StringComparison.OrdinalIgnoreCase);
            msgPanel.Children.Add(new Border
            {
                Background = Brush.Parse(isAdmin ? "#E8F0E9" : "#EAE5D9"),
                BorderBrush = Brush.Parse(isAdmin ? "#4A7C59" : "#C9C2B4"),
                BorderThickness = new Thickness(isAdmin ? 1.5 : 0),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6),
                Child = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = isAdmin ? $"管理员回执 · {m.CreatedAt}" : $"我的留言 · {m.CreatedAt}",
                            FontSize = 11,
                            FontWeight = isAdmin ? FontWeight.SemiBold : FontWeight.Normal,
                            Foreground = Brush.Parse(isAdmin ? "#2D5A3D" : "#8B8370"),
                        },
                        new TextBlock
                        {
                            Text = m.Content,
                            FontSize = 13,
                            Foreground = Brush.Parse("#2D3A2C"),
                            TextWrapping = TextWrapping.Wrap,
                        },
                    },
                },
            });
        }
        if (msgPanel.Children.Count == 0)
            msgPanel.Children.Add(new TextBlock
            {
                Text = "暂无回执",
                FontSize = 12,
                Foreground = Brush.Parse("#8B8370"),
            });

        var replyBox = NewBox("", editable, "追加说明…");
        replyBox.IsVisible = editable;

        var form = new StackPanel { Spacing = 8 };

        if (!string.IsNullOrWhiteSpace(vm.Notice) && isNew)
        {
            form.Children.Add(new Border
            {
                Background = Brush.Parse("#F2E6C8"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 4),
                Child = new TextBlock
                {
                    Text = vm.Notice,
                    FontSize = 12,
                    Foreground = Brush.Parse("#5C5344"),
                    TextWrapping = TextWrapping.Wrap,
                },
            });
        }

        // 已有申请：回执置顶，避免被表单字段挤到下面看不见
        if (!isNew)
        {
            var adminCount = messages.Count(m =>
                string.Equals(m.SenderRole, "admin", StringComparison.OrdinalIgnoreCase));
            form.Children.Add(new TextBlock
            {
                Text = adminCount > 0 ? $"回执信息（管理员 {adminCount} 条）" : "回执信息",
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush.Parse("#2D3A2C"),
            });
            form.Children.Add(new Border
            {
                Background = Brush.Parse("#F5F2EA"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8),
                Child = new ScrollViewer
                {
                    MaxHeight = 220,
                    Content = msgPanel,
                },
            });
            if (editable)
            {
                form.Children.Add(Label("留言"));
                form.Children.Add(replyBox);
            }
            form.Children.Add(new Border
            {
                Height = 1,
                Background = Brush.Parse("#D9D2C5"),
                Margin = new Thickness(0, 6, 0, 4),
            });
        }

        form.Children.Add(Label("选择充值/套餐订单"));
        form.Children.Add(new ScrollViewer
        {
            MaxHeight = 120,
            Content = orderPanel,
        });
        form.Children.Add(Label("抬头类型"));
        form.Children.Add(titleTypeCombo);
        form.Children.Add(Label("发票抬头"));
        form.Children.Add(titleNameBox);
        form.Children.Add(Label("税号（企业必填）"));
        form.Children.Add(taxBox);
        form.Children.Add(Label("地址电话"));
        form.Children.Add(addrBox);
        form.Children.Add(Label("开户行账号"));
        form.Children.Add(bankBox);
        form.Children.Add(Label("发票备注"));
        form.Children.Add(remarkBox);
        form.Children.Add(Label("接收邮箱（与手机必填其一）"));
        form.Children.Add(emailBox);
        form.Children.Add(Label("接收手机"));
        form.Children.Add(phoneBox);

        if (isNew)
        {
            form.Children.Add(Label("回执交流"));
            form.Children.Add(new ScrollViewer { MaxHeight = 140, Content = msgPanel });
            form.Children.Add(Label("留言"));
            form.Children.Add(replyBox);
        }

        var errLabel = new TextBlock
        {
            Foreground = Brush.Parse("#B8443C"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        form.Children.Add(errLabel);

        var saveBtn = new Button
        {
            Content = isNew ? "提交申请" : "保存修改",
            Background = Brush.Parse("#4A7C59"),
            Foreground = Brushes.White,
            FontWeight = FontWeight.SemiBold,
            MinWidth = 100,
            MinHeight = 36,
            Padding = new Thickness(16, 8),
            CornerRadius = new CornerRadius(6),
            IsVisible = editable,
            IsEnabled = editable,
        };
        var closeBtn = new Button
        {
            Content = "关闭",
            Background = Brush.Parse("#D4E5D9"),
            Foreground = Brush.Parse("#3D4A3E"),
            MinWidth = 88,
            MinHeight = 36,
            Padding = new Thickness(16, 8),
            CornerRadius = new CornerRadius(6),
        };

        form.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { closeBtn, saveBtn },
        });

        var dlg = new Window
        {
            Title = isNew ? "新建发票" : $"发票申请 #{detail!.Id}（{detail.StatusLabel}）",
            Width = 520,
            Height = 640,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border
            {
                Padding = new Thickness(20),
                Child = new ScrollViewer { Content = form },
            },
        };

        closeBtn.Click += (_, _) => dlg.Close();
        saveBtn.Click += async (_, _) =>
        {
            errLabel.IsVisible = false;
            var ids = orderChecks.Where(x => x.Cb.IsChecked == true).Select(x => x.OrderId).ToList();
            if (ids.Count == 0)
            {
                errLabel.Text = "请至少勾选一笔订单";
                errLabel.IsVisible = true;
                return;
            }

            var body = new InvoiceCreateRequest
            {
                PaymentOrderIds = ids,
                TitleType = titleTypeCombo.SelectedIndex == 1 ? "company" : "personal",
                TitleName = titleNameBox.Text?.Trim() ?? "",
                TaxNo = taxBox.Text,
                AddressPhone = addrBox.Text,
                BankAccount = bankBox.Text,
                Remark = remarkBox.Text,
                Email = emailBox.Text,
                Phone = phoneBox.Text,
            };

            try
            {
                InvoiceDetail saved;
                if (isNew)
                    saved = await vm.SubmitCreateAsync(body);
                else
                    saved = await vm.SubmitUpdateAsync(detail!.Id, body);

                var reply = replyBox.Text?.Trim();
                if (!string.IsNullOrEmpty(reply) && saved.Editable)
                    await vm.SubmitMessageAsync(saved.Id, reply);

                dlg.Close();
            }
            catch (Exception ex)
            {
                errLabel.Text = ex.Message;
                errLabel.IsVisible = true;
            }
        };

        await dlg.ShowDialog(owner);
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = Brush.Parse("#5C5344"),
    };

    private static TextBox NewBox(string text, bool enabled, string? watermark = null) => new()
    {
        Text = text,
        FontSize = 13,
        IsEnabled = enabled,
        Watermark = watermark,
    };
}
