using Nexora.Core.Whatsapp;

namespace Nexora.Infra.Armazenamento;

public class OpcoesMidia
{
    /// <summary>Raiz onde os arquivos ficam. Em dev, uma pasta do projeto; em producao, um
    /// volume montado. Vira bucket S3/R2 na fase 2.</summary>
    public string Raiz { get; set; } = "midia";
}

/// <summary>Armazenamento em DISCO — a escolha da fase 1.
///
/// Object storage (MinIO em dev, R2 em producao), expurgo por retencao e URL assinada sao fase
/// 2; ate la um volume resolve, e o que importa e que o processador do webhook nao saiba a
/// diferenca. Ver IArmazenamentoMidia.
///
/// LIMITE CONHECIDO: nao escala horizontal (cada instancia teria o proprio disco) e nao tem
/// expurgo. Enquanto for instancia unica — mesma premissa do rate limit em memoria do bloco 1 —
/// esta de acordo com o resto do desenho.</summary>
public class ArmazenamentoDisco(OpcoesMidia opcoes) : IArmazenamentoMidia
{
    public async Task SalvarAsync(byte[] conteudo, string chave, CancellationToken ct)
    {
        var caminho = CaminhoDe(chave);
        Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
        await File.WriteAllBytesAsync(caminho, conteudo, ct);
    }

    public Task<Stream?> AbrirAsync(string chave, CancellationToken ct)
    {
        var caminho = CaminhoDe(chave);
        Stream? stream = File.Exists(caminho)
            ? new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.Read)
            : null;
        return Task.FromResult(stream);
    }

    /// <summary>Resolve a chave dentro da raiz e RECUSA qualquer coisa que escape dela.
    ///
    /// A chave e montada por nos (emp-{id}/{waIdSafe}.{ext}) e o waId ja vem higienizado, mas a
    /// checagem fica: o dia em que uma chave passar a vir de entrada externa, `../../` viraria
    /// leitura de arquivo arbitrario.</summary>
    private string CaminhoDe(string chave)
    {
        var raiz = Path.GetFullPath(opcoes.Raiz);
        var caminho = Path.GetFullPath(Path.Combine(raiz, chave));

        if (!caminho.StartsWith(raiz + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && caminho != raiz)
            throw new InvalidOperationException($"Chave de midia invalida: {chave}");

        return caminho;
    }
}
