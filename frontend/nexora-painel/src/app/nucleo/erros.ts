/** ===================== A FRASE DE ERRO DO PAINEL (BUG-XX, T5) =====================
 *  Quando o servidor explica a recusa, vale a frase dele (`{ erro }`). Quando não explica — sem
 *  internet, servidor fora do ar, permissão negada antes de chegar à regra —, a tela dizia "Não foi
 *  possível salvar." sem dizer o quê nem o que fazer. Aqui a frase diz as duas coisas.
 *
 *  `acao` é o que a pessoa tentou, com o objeto: "salvar o canal", "apagar a etapa".
 *  ================================================================================= */
export function erroAo(e: unknown, acao: string): string {
  const r = e as { status?: number; error?: { erro?: unknown } } | null | undefined;
  const doServidor = r?.error?.erro;
  if (typeof doServidor === 'string' && doServidor.trim() !== '') return doServidor;

  const status = r?.status;
  if (status === 0) {
    return `Não foi possível ${acao}: o Nexora não respondeu. Confira a internet e tente de novo.`;
  }
  if (status === 403) return `Você não tem permissão para ${acao}.`;
  if (status === 404) return `Não foi possível ${acao}: o item não existe mais. Atualize a página.`;
  if (status !== undefined && status >= 500) {
    return `Não foi possível ${acao} agora. Tente de novo em alguns minutos.`;
  }
  return `Não foi possível ${acao}. Tente de novo.`;
}
