import { OrigemLead } from './modelos';

/** O nome de cada origem na língua de quem lê — `meta_ads` é "Meta Ads", `manual` é "Cadastro
 *  manual".
 *
 *  ⚠️ UMA CÓPIA SÓ. Ele morava dentro do dashboard, privado, e a tela de importar precisava do
 *  mesmo mapa: copiar faria a origem nova aparecer com nome bonito num lugar e como `meta_ads` no
 *  outro — que foi exatamente o que aconteceu quando `meta_ads` entrou no servidor e o painel não
 *  soube. O `Record<OrigemLead, string>` é o que obriga: origem nova sem rótulo não compila. */
export const ROTULO_ORIGEM: Record<OrigemLead, string> = {
  instagram: 'Instagram',
  facebook: 'Facebook',
  whatsapp: 'WhatsApp',
  google: 'Google',
  site: 'Site',
  qrcode: 'QR Code',
  indicacao: 'Indicação',
  meta_ads: 'Meta Ads',
  manual: 'Cadastro manual',
  outro: 'Outro'
};

/** A origem pronta para a tela. Valor fora do mapa (origem nova no servidor antes do painel saber)
 *  aparece cru — melhor que sumir. */
export function rotuloOrigem(origem: string | null | undefined): string {
  if (!origem) return '';
  return ROTULO_ORIGEM[origem as OrigemLead] ?? origem;
}

/** O papel na tela: `dono` é "Dono". */
export function rotuloPapel(papel: string | null | undefined): string {
  if (papel === 'dono') return 'Dono';
  if (papel === 'gestor') return 'Gestor';
  if (papel === 'vendedor') return 'Vendedor';
  return papel ?? '';
}

/** Como o lembrete terminou, para a lista dos resolvidos. */
export function rotuloStatusLembrete(status: string): string {
  if (status === 'concluido') return 'Concluído';
  if (status === 'cancelado') return 'Cancelado';
  if (status === 'pendente') return 'Pendente';
  return status;
}

/** A qualidade do número que a Meta informa (`GREEN`, `YELLOW`, `RED`). */
export function rotuloQualidadeMeta(qualidade: string): string {
  const q = qualidade.toUpperCase();
  if (q === 'GREEN') return 'boa';
  if (q === 'YELLOW') return 'média';
  if (q === 'RED') return 'baixa';
  return 'ainda sem avaliação';
}
