import { Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { IntegracaoWebhook } from './webhook/webhook';
import { IntegracaoAnuncios } from './anuncios/anuncios';

/** UI-XX · configurar e acompanhar em abas separadas: cada painel mistura os dois, e a tela
 *  ficava longa demais para quem só queria olhar o que foi enviado. */
export type AbaIntegracoes = 'anuncios' | 'envios' | 'webhook' | 'entregas';

/** INTEGRAÇÕES — o que o Nexora conversa com o resto do mundo.
 *
 *  ===================== POR QUE ABA, E NÃO UMA TELA POR PARCEIRO (INT-4) =====================
 *  "Conversões de anúncio" e "webhook de saída" respondem à MESMA pergunta do cliente: o que este
 *  sistema fala com o que eu já uso. Uma tela por parceiro faria o menu crescer um item por
 *  integração — e o dono, que abre isto uma vez por trimestre, teria de aprender onde cada uma mora.
 *
 *  Mesmo movimento que a Captação fez com QR e formulário, pela mesma razão.
 *  ==========================================================================================
 *
 *  ===================== ESTE COMPONENTE FAZ CABEÇALHO E ABAS, E SÓ =====================
 *  Os dois painéis buscam os próprios dados e funcionam sozinhos — é assim que são testados, e é
 *  o que evita transformar o container num componente que sabe demais.
 *
 *  Não há resumo comum aqui, ao contrário da Captação: lá os dois números são comparáveis ("o
 *  panfleto trouxe mais que a landing page?"). Aqui, "entregas de webhook" e "conversões
 *  enviadas" não se comparam com nada — somá-los seria um número que não responde pergunta
 *  nenhuma.
 *  ====================================================================================== */
@Component({
  selector: 'app-integracoes',
  imports: [IntegracaoWebhook, IntegracaoAnuncios],
  templateUrl: './integracoes.html',
  styleUrl: './integracoes.css'
})
export class Integracoes implements OnInit {
  private rota = inject(ActivatedRoute);
  private router = inject(Router);
  private auth = inject(AuthServico);

  /** O webhook abre por padrão: é a integração que já existia, e quem tem link salvo para
   *  `/integracoes` espera cair nela. */
  /// ⚠️ ANÚNCIOS É A ABA PADRÃO, e a ordem dos botões acompanha. É a que serve a todo cliente:
  /// conectar o pixel faz a venda fechada aqui voltar para a Meta, e isso vale para quem só tem
  /// WhatsApp. O webhook é o extra — só serve a quem tem OUTRO sistema para avisar, que é a
  /// minoria, e quem precisa dele sabe que precisa.
  ///
  /// Abrir na aba que a maioria não usa faz a tela parecer não ser para ela.
  aba = signal<AbaIntegracoes>('anuncios');

  /** ⚠️ AS DO WEBHOOK SÓ PARA QUEM CONFIGURA A EMPRESA (BUG-XX). A tela abre com "gerenciar
   *  anúncios", mas a API do webhook exige "configurar empresa": o gestor com só a primeira via
   *  as duas abas e recebia "sem permissão" ao abrir. */
  readonly abas: { id: AbaIntegracoes; rotulo: string }[] = [
    { id: 'anuncios', rotulo: 'Anúncios' },
    { id: 'envios', rotulo: 'Envios à Meta' },
    ...(this.auth.pode('configurar_empresa')
      ? [{ id: 'webhook' as const, rotulo: 'Webhook' }, { id: 'entregas' as const, rotulo: 'Entregas do webhook' }]
      : [])
  ];

  /** As falhas que cada painel conta ao carregar, no rótulo da aba do histórico dele. Zero até o
   *  painel daquela integração abrir — e ele abre junto com a aba vizinha. */
  falhasMeta = signal(0);
  falhasWebhook = signal(0);

  ngOnInit() {
    const pedida = this.rota.snapshot.queryParamMap.get('aba');
    for (const a of this.abas) {
      if (a.id === pedida) this.aba.set(a.id);
    }
  }

  falhasDa(aba: AbaIntegracoes): number {
    if (aba === 'envios') return this.falhasMeta();
    if (aba === 'entregas') return this.falhasWebhook();
    return 0;
  }

  trocarAba(aba: AbaIntegracoes) {
    if (this.aba() === aba) return;
    this.aba.set(aba);

    // `replaceUrl`: trocar de aba não é navegação para o histórico. Sem isto, o botão "voltar" do
    // navegador percorreria as abas antes de sair da tela.
    this.router.navigate([], {
      relativeTo: this.rota,
      // O padrão sai da URL: `/integracoes` limpo já é Anúncios, e só o webhook precisa de
      // `?aba=`. Sem isto, a aba padrão carregaria um parâmetro que não diz nada.
      queryParams: { aba: aba === 'anuncios' ? null : aba },
      queryParamsHandling: 'merge',
      replaceUrl: true
    });
  }
}
