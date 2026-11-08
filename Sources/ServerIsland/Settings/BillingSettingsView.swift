import SwiftUI

/// 设置 → 计费：总开关、货币、每台调度系统服务器的分区单价（可以从服务器读取分区）。
struct BillingSettingsView: View {
    @ObservedObject var store: ConfigStore
    @ObservedObject var engine: MonitorEngine
    @State private var loading: Set<UUID> = []
    @State private var errors: [UUID: String] = [:]
    @State private var confirmClear = false

    static let currencies = ["¥", "$", "€", "£"]

    private var schedulerHosts: [HostConfig] { store.settings.hosts.filter(\.kind.isScheduler) }

    var body: some View {
        Form {
            Section {
                Toggle(isOn: $store.settings.billingEnabled) {
                    HelpLabel(L("估算费用", "Estimate Costs"),
                              help: L("按你填的单价 × 作业分到的资源 × 运行时长估算花了多少钱。只算 Slurm / PBS 作业；运行中的作业会额外查一次分到的资源（每个作业一次）。结果是估算，和计算中心的账单可能有出入。",
                                      "Price × allocated resources × run time. Slurm / PBS jobs only; each running job's allocation is queried once. This is an estimate and may differ from your computing center's bill."))
                }
                if store.settings.billingEnabled {
                    Picker(L("货币", "Currency"), selection: $store.settings.currency) {
                        ForEach(Self.currencies, id: \.self) { Text($0).tag($0) }
                    }
                    let s = CostSummary(engine.costEntriesWithRunning())
                    LabeledContent(L("本月 / 上月", "This / last month")) {
                        Text(Billing.money(s.month, currency: store.settings.currency) + "  /  " + Billing.money(s.lastMonth, currency: store.settings.currency))
                            .monospacedDigit()
                    }
                    HStack {
                        Spacer()
                        Button(L("清除费用记录…", "Clear Cost Records…")) { confirmClear = true }
                            .confirmationDialog(L("清除全部费用记录？", "Clear all cost records?"), isPresented: $confirmClear) {
                                Button(L("清除", "Clear"), role: .destructive) { engine.costs.clear() }
                            } message: {
                                Text(L("本机记下的费用都会删除（单价设置保留）。", "All cost records on this Mac will be deleted. Prices are kept."))
                            }
                    }
                }
            } header: {
                SettingsHeader(title: L("计费", "Billing"), symbol: "creditcard.fill", tint: .orange)
            } footer: {
                Text(Billing.disclaimer).font(.caption).foregroundStyle(.secondary)
            }

            if store.settings.billingEnabled {
                if schedulerHosts.isEmpty {
                    Section {
                        Text(L("还没有 Slurm / PBS 服务器。直连服务器没有调度系统的分配记录，不计费。",
                               "No Slurm / PBS servers yet. Direct servers have no scheduler allocation, so they aren't billed."))
                            .foregroundStyle(.secondary)
                    }
                }
                ForEach(schedulerHosts) { host in hostSection(host) }
            }
        }
        .formStyle(.grouped)
        .navigationTitle(L("计费", "Billing"))
    }

    // MARK: - 每台服务器

    private func hostSection(_ host: HostConfig) -> some View {
        Section {
            ForEach(rows(host)) { p in priceRow(host, p) }
            HStack(spacing: 8) {
                Button {
                    Task { await readPartitions(host) }
                } label: {
                    Label(host.kind == .pbs ? L("从服务器读取队列", "Read Queues from Server") : L("从服务器读取分区", "Read Partitions from Server"),
                          systemImage: "arrow.down.circle")
                }
                .disabled(loading.contains(host.id))
                .help(host.kind == .pbs ? L("只读：qstat -Q", "Read-only: qstat -Q") : L("只读：sinfo（和分区页同一个查询）", "Read-only: sinfo (same query as the Partitions page)"))
                Button { addRow(host) } label: { Label(L("添加分区", "Add Partition"), systemImage: "plus") }
                if loading.contains(host.id) { ProgressView().controlSize(.small) }
                Spacer()
                if let e = errors[host.id] {
                    Label(e, systemImage: "exclamationmark.triangle.fill").font(.caption).foregroundStyle(.orange).lineLimit(1)
                }
            }
        } header: {
            Text(host.displayName + " · " + host.kind.schedulerName)
        } footer: {
            Text(L("单价填 0 的分区不计费。没有单独设置的分区按“其他分区”计算。", "Partitions priced at 0 aren't billed. Partitions without their own row use “Other partitions”."))
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    /// 显示的行："其他分区"总在第一行（还没有时临时补一行，填了价格才写进配置），后面是各分区。
    private func rows(_ host: HostConfig) -> [PartitionPrice] {
        let fallback = host.prices.first { $0.partition.isEmpty } ?? PartitionPrice(partition: "", unit: .gpu, price: 0)
        return [fallback] + host.prices.filter { !$0.partition.isEmpty }
    }

    private func priceRow(_ host: HostConfig, _ p: PartitionPrice) -> some View {
        let isDefault = p.partition.isEmpty
        return HStack(spacing: 10) {
            VStack(alignment: .leading, spacing: 1) {
                if isDefault {
                    Text(L("其他分区", "Other partitions"))
                } else {
                    TextField("", text: binding(host, p, \.partition), prompt: Text(L("分区名", "Partition")))
                        .labelsHidden()
                        .textFieldStyle(.plain)
                        .font(.body.monospaced())
                }
                if !p.note.isEmpty {
                    Text(p.note).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                }
            }
            Spacer(minLength: 8)
            Picker("", selection: binding(host, p, \.unit)) {
                ForEach(PriceUnit.allCases) { Text($0.label).tag($0) }
            }
            .labelsHidden()
            .frame(width: 120)
            Text(store.settings.currency).foregroundStyle(.secondary)
            TextField("", value: binding(host, p, \.price), format: .number.precision(.fractionLength(0...4)), prompt: Text("0"))
                .labelsHidden()
                .multilineTextAlignment(.trailing)
                .frame(width: 70)
            Text("/" + p.unit.perLabel).foregroundStyle(.secondary).frame(width: 62, alignment: .leading)
            if !isDefault {
                Button { remove(host, p) } label: { Image(systemName: "minus.circle.fill").foregroundStyle(.secondary) }
                    .buttonStyle(.plain)
                    .help(L("删除这一行", "Remove this row"))
            } else {
                Color.clear.frame(width: 16, height: 1)
            }
        }
    }

    // MARK: - 修改配置

    private func hostIndex(_ id: UUID) -> Int? { store.settings.hosts.firstIndex { $0.id == id } }

    /// 按服务器和行的 id 绑定；"其他分区"还不存在时，第一次修改才把它加进配置。
    private func binding<T>(_ host: HostConfig, _ p: PartitionPrice, _ key: WritableKeyPath<PartitionPrice, T>) -> Binding<T> {
        Binding(
            get: {
                guard let i = hostIndex(host.id) else { return p[keyPath: key] }
                return store.settings.hosts[i].prices.first { $0.id == p.id }?[keyPath: key] ?? p[keyPath: key]
            },
            set: { v in
                guard let i = hostIndex(host.id) else { return }
                if let j = store.settings.hosts[i].prices.firstIndex(where: { $0.id == p.id }) {
                    store.settings.hosts[i].prices[j][keyPath: key] = v
                } else {
                    var n = p
                    n[keyPath: key] = v
                    store.settings.hosts[i].prices.insert(n, at: 0)
                }
            })
    }

    private func addRow(_ host: HostConfig) {
        guard let i = hostIndex(host.id) else { return }
        store.settings.hosts[i].prices.append(PartitionPrice(partition: "", unit: .gpu, price: 0))
        // 空名字会被当成"其他分区"：给一个占位名，用户再改
        let n = store.settings.hosts[i].prices.count
        store.settings.hosts[i].prices[n - 1].partition = L("新分区", "new") + (n > 1 ? "\(n)" : "")
    }

    private func remove(_ host: HostConfig, _ p: PartitionPrice) {
        guard let i = hostIndex(host.id) else { return }
        store.settings.hosts[i].prices.removeAll { $0.id == p.id }
    }

    /// 读到的分区补进列表（已有的只更新说明，不动单价）；有卡的分区默认按卡时，否则按核时。
    private func readPartitions(_ host: HostConfig) async {
        loading.insert(host.id)
        errors[host.id] = nil
        let r = await engine.fetchBillingPartitions(host: host)
        loading.remove(host.id)
        if let e = r.error { errors[host.id] = e; return }
        guard let i = hostIndex(host.id) else { return }
        for item in r.list {
            if let j = store.settings.hosts[i].prices.firstIndex(where: { $0.partition == item.name }) {
                store.settings.hosts[i].prices[j].note = item.note
            } else {
                store.settings.hosts[i].prices.append(PartitionPrice(partition: item.name, unit: item.hasGPU ? .gpu : .cpu, price: 0, note: item.note))
            }
        }
        if r.list.isEmpty { errors[host.id] = L("没有读到分区", "No partitions found") }
    }
}
