using System.Net;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Spark108.Core;

namespace Spark108.Server;

internal static class AdminView
{
    private static string H(string? text) => WebUtility.HtmlEncode(text ?? "");
    private static string Checked(bool value) => value ? " checked" : "";
    private static string Kind(PackageKind kind) => kind switch
    {
        PackageKind.Component => "Компонент", PackageKind.AppCenter => "Системный клиент", _ => "Программа"
    };

    private static IResult Page(string title, string content, HttpContext? context = null, int status = 200)
    {
        var navigation = context is null ? "" : $"""
            <header><a class="brand" href="/acp"><b>✦</b> <span>Центр приложений</span></a>
              <nav><a href="/acp">Приложения</a><a href="/acp/users">Пользователи</a>
              <form method="post" action="/acp/logout">{Csrf(context)}<button class="quiet">Выйти</button></form></nav></header>
            """;
        return Results.Content($"""
            <!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{H(title)} · Spark108</title><link rel="stylesheet" href="/admin.css"></head>
            <body>{navigation}<main>{content}</main></body></html>
            """, "text/html", Encoding.UTF8, status);
    }

    private static string Csrf(HttpContext context)
    {
        const string key = "acp.csrf";
        if (context.Items[key] is not AntiforgeryTokenSet tokens)
        {
            tokens = context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context);
            context.Items[key] = tokens;
        }
        return $"""<input type="hidden" name="{H(tokens.FormFieldName)}" value="{H(tokens.RequestToken)}">""";
    }

    public static IResult Message(int status, string title, string description) =>
        Page(title, $"""<section class="message"><h1>{H(title)}</h1><p>{H(description)}</p>{(status == 404 ? "" : """<a class="button" href="/acp">Открыть панель</a>""")}</section>""", status: status);

    public static IResult Login() => Page("Вход", """
        <section class="message"><div class="eyebrow">✦ Центр приложений</div><h1>Панель управления</h1>
        <p>Войдите с учётной записью Alathea Auth.</p><a class="button primary" href="/acp/login">Войти через Alathea</a></section>
        """);

    public static IResult Dashboard(HttpContext c, CatalogSnapshot data)
    {
        var rows = string.Concat(data.Apps.Values.Select(app => $"""
            <tr><td><a class="item" href="/acp/apps/{app.Id}">{H(app.Name)}</a><small>{app.Id}</small></td>
            <td>{Kind(app.Kind)}</td><td>{H(data.Releases[app.Id].FirstOrDefault(x => x.Published)?.Info.Version ?? "—")}</td>
            <td>{(app.Restricted ? "По списку" : "Всем пользователям")}</td></tr>
            """));
        return Page("Приложения", $"""
            <div class="heading"><div><div class="eyebrow">Каталог</div><h1>Приложения</h1></div>
            <div class="heading-actions"><a class="button" href="/acp/installer">Установщик Windows</a>
            <a class="button primary" href="/acp/apps/new">Добавить приложение</a></div></div>
            <div class="table"><table><thead><tr><th>Название</th><th>Тип</th><th>Опубликованная версия</th><th>Доступ</th></tr></thead>
            <tbody>{rows}</tbody></table></div>
            """, c);
    }

    public static IResult Edit(HttpContext c, AppRow? app, CatalogSnapshot data, UserProfile[] users, long[] access)
    {
        var title = app?.Name ?? "Новое приложение";
        var path = "/acp/apps/" + (app?.Id ?? "new");
        var csrf = Csrf(c);
        var identity = app is null ? """
            <label>Идентификатор<input name="id" required maxlength="64" pattern="[a-z0-9][a-z0-9-]*" placeholder="infra-tool"></label>
            <label>Тип<select name="kind"><option value="application">Программа</option><option value="component">Служебный компонент</option></select></label>
            """ : $"""<div class="meta">{app.Id} · {Kind(app.Kind)}</div>""";
        var dependencies = data.Apps.Values.Where(x => x.Kind == PackageKind.Component && x.Id != app?.Id).ToArray();
        var depSelection = app is null ? new HashSet<string>() : data.Dependencies[app.Id].ToHashSet();
        var depOptions = string.Concat(dependencies.Select(x => $"""
            <label class="check"><input type="checkbox" name="dependency" value="{x.Id}"{Checked(depSelection.Contains(x.Id))}>{H(x.Name)}</label>
            """));
        var accessOptions = string.Concat(users.Select(user => $"""
            <label class="check"><input type="checkbox" name="access" value="{user.Id}"{Checked(access.Contains(user.Id))}>
            {H(user.Name)} <small>{H(user.Email)}</small></label>
            """));
        var releases = app is null ? "" : Releases(app, path, csrf, data.Releases[app.Id]);
        return Page(title, $"""
            <a class="back" href="/acp">← Все приложения</a><h1>{H(title)}</h1>
            <form class="panel" method="post" action="{path}">{csrf}<h2>Данные приложения</h2>
              <div class="fields">{identity}
              <label>Название<input name="name" required maxlength="120" value="{H(app?.Name)}"></label>
              <label>Описание<textarea name="description" rows="5" maxlength="8000">{H(app?.Description)}</textarea></label></div>
              <div class="split"><fieldset><legend>Зависимости</legend>
              {(depOptions.Length == 0 ? "<p>Служебные компоненты ещё отсутствуют.</p>" : depOptions)}</fieldset>
              <fieldset><legend>Доступ к приложению</legend><label class="check"><input name="restricted" type="checkbox"{Checked(app?.Restricted == true)}>Ограничить доступ списком</label>
              <div class="options">{accessOptions}</div></fieldset></div>
              <button class="primary">Сохранить приложение</button>
            </form>{releases}
            """, c);
    }

    private static string Releases(AppRow app, string path, string csrf, IEnumerable<ReleaseRow> releases)
    {
        var entry = app.Kind == PackageKind.Application
            ? """<label>Файл запуска<input name="entry" maxlength="400" placeholder="bin/Tool.exe или {installDir}/Tool.exe"></label>""" : "";
        var installer = app.Kind == PackageKind.AppCenter ? "" : """
            <details><summary>Собственный установщик</summary>
            <div class="fields">
              <label>Установщик внутри ZIP<input name="installer" maxlength="240" placeholder="setup/setup.exe, setup.msi или install.ps1"></label>
              <label>Аргументы установки, по одному на строку<textarea name="install_args" rows="3" maxlength="8000"></textarea></label>
              <label class="check"><input type="checkbox" name="install_elevated">Запрашивать права администратора при установке</label>
              <label>Файл удаления<input name="uninstaller" maxlength="400" placeholder="uninstall.ps1 или {installDir}/uninstall.exe"></label>
              <label>Аргументы удаления, по одному на строку<textarea name="uninstall_args" rows="3" maxlength="8000"></textarea></label>
              <label class="check"><input type="checkbox" name="uninstall_elevated">Запрашивать права администратора при удалении</label>
              <label>Имя ключа удаления Windows<input name="registry_key" maxlength="160" placeholder="{GUID}"></label>
              <small>{installDir} — папка программы, управляемая Центром приложений. {packageDir} — папка пакета.</small>
            </div></details>
            """;
        var rows = string.Concat(releases.Select(row => $"""
            <form class="panel release" method="post" action="{path}/releases/{row.Info.Id}">{csrf}
              <div><h3>{H(row.Info.Version)}</h3><small>{row.Info.Size / (1024d * 1024):0.0} МБ · {(row.Published ? "Опубликована" : "Черновик")}</small>
              <a class="back" href="{path}/releases/{row.Info.Id}/package">Скачать ZIP</a></div>
              <label>Что нового<textarea name="notes" rows="2" maxlength="8000">{H(row.Info.Notes)}</textarea>
              {(row.Info.Installer is null ? "" : $"<small>Установщик · {H(row.Info.Installer.Install.Path)}</small>")}</label>
              <label class="check"><input type="checkbox" name="published"{Checked(row.Published)}>Опубликована</label>
              <button>Сохранить</button>
            </form>
            """));
        return $"""
            <h2 id="releases">Версии</h2>
            <form class="panel fields" method="post" action="{path}/releases" enctype="multipart/form-data">{csrf}
              <h3>Загрузить версию</h3><div class="split">
              <label>Версия<input required name="version" placeholder="1.2.3" maxlength="32"></label>
              <label>ZIP-пакет<input required type="file" name="package" accept=".zip,application/zip"></label></div>
              {entry}{installer}<label>Что нового<textarea name="notes" rows="3" maxlength="8000"></textarea></label>
              <label class="check"><input type="checkbox" name="published">Опубликовать сразу</label>
              <div><button class="primary">Загрузить</button></div>
            </form>{rows}
            """;
    }

    public static IResult Users(HttpContext c, UserProfile[] users, long actor)
    {
        var csrf = Csrf(c);
        var rows = string.Concat(users.Select(user => $"""
            <form class="panel user" method="post" action="/acp/users/{user.Id}">{csrf}
              <div><b>{H(user.Name)}</b><small>{H(user.Email)}</small><small>Alathea ID {H(user.Subject)}</small></div>
              {(user.Id == actor ? "<span>Текущий администратор</span>" : $"""
              <label class="check"><input type="checkbox" name="admin"{Checked(user.IsAdmin)}>Администратор</label>
              <label class="check"><input type="checkbox" name="enabled"{Checked(user.Enabled)}>Вход разрешён</label>
              <button>Сохранить</button>
              """)}
            </form>
            """));
        return Page("Пользователи", $"""
            <div class="heading"><div><h1>Пользователи</h1><p>Учётная запись появляется здесь после первого входа через Alathea Auth.</p></div></div>
            {rows}
            """, c);
    }
}
