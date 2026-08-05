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
