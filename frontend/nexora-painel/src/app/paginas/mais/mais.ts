import { Component, computed, inject } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { OnboardingServico } from '../../nucleo/servicos/onboarding.servico';
import { PainelServico } from '../../nucleo/servicos/painel.servico';

/** A TELA "MAIS" — o resto do menu, no celular.
 *
 *  ===================== POR QUE ELA EXISTE (MOB-2) =====================
 *  A barra inferior tem cinco lugares e o painel tem treze destinos. Os quatro primeiros são o que
 *  o vendedor abre todo dia; o resto mora aqui.
 *
 *  ⚠️ A BARRA É IGUAL PARA TODO PAPEL, e é ESTA tela que faz o recorte. Barra que muda de conteúdo
 *  quando o vendedor vira gestor apaga a memória muscular dele — o item do terceiro lugar passa a
 *  ser outro e o dedo erra por semanas. Aqui a lista simplesmente encurta, e ninguém decorou a
 *  posição de uma lista que se lê.
 *
 *  No desktop ela é alcançável pela URL, mas nada aponta para cá: lá a lateral mostra os treze de
 *  uma vez. */
@Component({
  selector: 'app-mais',
  imports: [RouterLink, RouterLinkActive],
  templateUrl: './mais.html',
  styleUrl: './mais.css'
})
export class Mais {
  auth = inject(AuthServico);
  onboarding = inject(OnboardingServico);
  private painel = inject(PainelServico);

  /** O MESMO ponto de status do menu lateral, e pela mesma razão: o estado da coisa fica junto do
   *  link que leva até ela. Sai do status que o shell já busca — sem requisição nova. */
  statusConexao = computed<'ok' | 'verificando' | 'caiu'>(() => {
    const s = this.painel.ultimo();
    if (s === null) return 'verificando';
    return s.whatsappConectado ? 'ok' : 'caiu';
  });

  rotuloConexao = computed(() => {
    switch (this.statusConexao()) {
      case 'ok': return 'WhatsApp conectado';
      case 'caiu': return 'WhatsApp desconectado';
      default: return 'Verificando a conexão…';
    }
  });
}
