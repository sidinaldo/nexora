/** Gera um .csv no navegador e dispara o download. Sem backend.
 *
 *  - Separador ';' (padrão do Excel brasileiro)
 *  - BOM UTF-8 (﻿) para o Excel abrir os acentos do cabeçalho corretamente
 *  - Quebra de linha CRLF (o que o Excel gera) */
export function baixarCsv(nomeArquivo: string, linhas: string[][]): void {
  const csv = linhas.map(l => l.map(campo).join(';')).join('\r\n');
  const blob = new Blob(['﻿' + csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = nomeArquivo;
  a.click();
  URL.revokeObjectURL(url);
}

/** Escapa um campo só quando precisa (contém ';', aspas ou quebra de linha). */
function campo(valor: string): string {
  return /[";\r\n]/.test(valor) ? `"${valor.replace(/"/g, '""')}"` : valor;
}

/** Dispara o download de um arquivo que veio da API.
 *
 *  ===================== POR QUE NÃO UM `<a href="/api/...">` =====================
 *  As rotas do painel exigem `Authorization: Bearer`, e navegação direta não carrega cabeçalho —
 *  o link abriria um 401. O arquivo é baixado pelo HttpClient (que passa pelo interceptor de
 *  autenticação) e só então vira download.
 *
 *  `revokeObjectURL` fica no fim: sem ele cada download deixa o blob preso na memória da aba
 *  até o recarregamento, e uma tela de QR com dez canais é dez blobs de imagem. */
export function baixarBlob(nomeArquivo: string, blob: Blob): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = nomeArquivo;
  a.click();
  URL.revokeObjectURL(url);
}
